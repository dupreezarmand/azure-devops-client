using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace DevOpsClient.Core;

public sealed partial class AzureDevOpsService
{
    public async Task<WorkQueue> LoadAssignedAsync(CancellationToken token)
    {
        using var query = await SendAsync(HttpMethod.Post, $"_apis/wit/wiql?$top={settings.MaxWorkItems}&api-version=7.1", JsonContent.Create(new {
            query = "Select [System.Id] From WorkItems Where [System.TeamProject] = @project And [System.AssignedTo] = @Me And [System.State] <> 'Removed' Order By [System.ChangedDate] Desc"
        }), token);
        var ids = query.RootElement.GetProperty("workItems").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray();
        var items = await ReadBatchAsync(ids, "Fail", token);
        return new(items.OrderByDescending(i => i.ChangedAt).ToArray(), ids.Length >= settings.MaxWorkItems);
    }

    public async Task<WorkItemDetails> GetDetailsAsync(int id, CancellationToken token)
    {
        if (id <= 0) throw new ArgumentException("Enter a positive work item number.");
        using var doc = await SendAsync(HttpMethod.Get, $"_apis/wit/workitems/{id}?$expand=relations&api-version=7.1", null, token);
        var item = ParseItem(doc.RootElement);
        var links = new List<(int Id, string Name)>();
        if (doc.RootElement.TryGetProperty("relations", out var relations))
        {
            foreach (var relation in relations.EnumerateArray())
            {
                var kind = Text(relation, "rel");
                if (!kind.StartsWith("System.LinkTypes.", StringComparison.Ordinal)) continue;
                // Extract IDs only from this organization's own work item URLs. Never fetch a server-provided URL with the PAT.
                if (!TryRelatedId(Text(relation, "url"), out var relatedId)) continue;
                var name = kind switch {
                    "System.LinkTypes.Hierarchy-Reverse" => "Parent", "System.LinkTypes.Hierarchy-Forward" => "Child",
                    "System.LinkTypes.Related" => "Related", "System.LinkTypes.Dependency-Reverse" => "Predecessor",
                    "System.LinkTypes.Dependency-Forward" => "Successor", "System.LinkTypes.Duplicate-Reverse" => "Duplicate of",
                    "System.LinkTypes.Duplicate-Forward" => "Duplicate", _ => kind["System.LinkTypes.".Length..] };
                links.Add((relatedId, name));
            }
        }
        IReadOnlyList<WorkItem> related = [];
        string? warning = null;
        try { related = await ReadBatchAsync(links.Select(l => l.Id).Distinct().ToArray(), "Omit", token); }
        catch (HttpRequestException) { warning = "Some linked items could not be read. Check project permissions or retry."; }
        var byId = related.ToDictionary(i => i.Id);
        var result = links.Select(l => new RelatedWorkItem(l.Id, l.Name, byId.GetValueOrDefault(l.Id))).ToArray();
        if (result.Any(r => r.Item == null)) warning ??= "Some linked items are unavailable, deleted, or outside your access.";
        return new(item, result, warning);
    }

    private bool TryRelatedId(string url, out int id)
    {
        id = 0;
        var project = settings.ProjectUri();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.Host != project.Host || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        var path = uri.AbsolutePath;
        if (project.Host == "dev.azure.com")
        {
            var orgPrefix = "/" + project.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0] + "/";
            if (!path.StartsWith(orgPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        var match = Regex.Match(path, @"/_apis/wit/workitems/(\d+)$", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out id) && id > 0;
    }

    private async Task<IReadOnlyList<WorkItem>> ReadBatchAsync(int[] ids, string policy, CancellationToken token)
    {
        var items = new List<WorkItem>();
        foreach (var batch in ids.Chunk(200))
        {
            using var doc = await SendAsync(HttpMethod.Post, "_apis/wit/workitemsbatch?api-version=7.1", JsonContent.Create(new { ids = batch, errorPolicy = policy }), token);
            items.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseItem));
        }
        return items;
    }

    public async Task<CommentPage> GetCommentsAsync(int id, string? continuationToken, CancellationToken token)
    {
        if (id <= 0) throw new ArgumentException("Enter a positive work item number.");
        var continuation = string.IsNullOrEmpty(continuationToken) ? "" : "&continuationToken=" + Uri.EscapeDataString(continuationToken);
        using var doc = await SendAsync(HttpMethod.Get, $"_apis/wit/workitems/{id}/comments?$top=50&order=asc&$expand=renderedText&includeDeleted=false{continuation}&api-version=7.1-preview.4", null, token);
        var root = doc.RootElement;
        var comments = root.GetProperty("comments").EnumerateArray()
            .Where(c => !c.TryGetProperty("isDeleted", out var deleted) || !deleted.GetBoolean())
            .Select(ParseComment).ToArray();
        return new(comments, Text(root, "continuationToken") is { Length: > 0 } next ? next : null, (int)Number(root, "totalCount", comments.Length));
    }

    public async Task<WorkItemComment> AddCommentAsync(int id, string text, CancellationToken token)
    {
        ValidateComment(id, text);
        using var doc = await SendAsync(HttpMethod.Post, $"_apis/wit/workitems/{id}/comments?format=markdown&api-version=7.1-preview.4",
            CommentBody(text), token);
        var added = ParseComment(doc.RootElement);
        return added with { EditableText = added.EditableText.Length > 0 ? added.EditableText : text, IsMarkdown = true };
    }

    public async Task<WorkItemComment> UpdateCommentAsync(int id, WorkItemComment comment, string text, CancellationToken token)
    {
        ValidateComment(id, text);
        if (comment.Id <= 0) throw new ArgumentException("Choose a valid comment to edit.");
        var format = comment.IsMarkdown || !comment.IsHtml ? "markdown" : "html";
        using var doc = await SendAsync(HttpMethod.Patch, $"_apis/wit/workitems/{id}/comments/{comment.Id}?format={format}&api-version=7.1-preview.4",
            CommentBody(text), token);
        var updated = ParseComment(doc.RootElement);
        return updated with
        {
            EditableText = updated.EditableText.Length > 0 ? updated.EditableText : text,
            IsMarkdown = format == "markdown",
            IsHtml = updated.IsHtml || format == "html"
        };
    }

    private static void ValidateComment(int id, string text)
    {
        if (id <= 0) throw new ArgumentException("Enter a positive work item number.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a comment before saving.");
        if (text.Length > 150_000) throw new ArgumentException("The comment is too large to save.");
    }

    private static StringContent CommentBody(string text) =>
        new(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");

    private static WorkItemComment ParseComment(JsonElement comment)
    {
        var raw = Text(comment, "text");
        var rendered = Text(comment, "renderedText");
        var format = Text(comment, "format");
        var markdown = format.Equals("markdown", StringComparison.OrdinalIgnoreCase);
        var html = rendered.Length > 0 || format.Equals("html", StringComparison.OrdinalIgnoreCase);
        return new((int)Number(comment, "id", Number(comment, "commentId")),
            comment.TryGetProperty("createdBy", out var author) ? Text(author, "displayName", "Unknown author") : "Unknown author",
            DateTimeOffset.TryParse(Text(comment, "createdDate"), out var date) ? date : DateTimeOffset.MinValue,
            rendered.Length > 0 ? rendered : raw, html, raw, markdown, (int)Number(comment, "version", 1));
    }
}
