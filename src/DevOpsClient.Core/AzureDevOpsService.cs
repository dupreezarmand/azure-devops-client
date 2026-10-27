using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
namespace DevOpsClient.Core;

public sealed partial class AzureDevOpsService : IProjectService
{
    private readonly HttpClient client;
    private readonly ClientSettings settings;
    private IReadOnlyList<WorkItemType> types = [];
    public AzureDevOpsService(ClientSettings settings, HttpMessageHandler? handler = null, Uri? endpointOverride = null)
    {
        settings.Validate();
        this.settings = settings;
        client = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = endpointOverride ?? settings.ProjectUri(), Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + settings.PersonalAccessToken)));
        client.DefaultRequestHeaders.Accept.Add(new("application/json"));
    }
    public async Task<ProjectSnapshot> LoadAsync(CancellationToken token)
    {
        using (var typeDoc = await SendAsync(HttpMethod.Get, "_apis/wit/workitemtypes?api-version=7.1", null, token))
        {
            types = typeDoc.RootElement.GetProperty("value").EnumerateArray()
                .Where(t => !t.TryGetProperty("isDisabled", out var d) || !d.GetBoolean())
                .Select(t => new WorkItemType(Text(t, "name"), t.TryGetProperty("states", out var states)
                    ? states.EnumerateArray().Select(s => new StateDefinition(Text(s, "name"), Text(s, "category", "Unknown"))).ToArray() : [])).ToArray();
        }
        // Some service versions omit category metadata from the type-list response.
        var resolvedTypes = types.ToArray();
        await Parallel.ForEachAsync(Enumerable.Range(0, resolvedTypes.Length), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, ct) =>
        {
            var type = resolvedTypes[index];
            if (type.States.Count > 0 && type.States.All(s => s.Category != "Unknown")) return;
            using var states = await SendAsync(HttpMethod.Get, "_apis/wit/workitemtypes/" + Uri.EscapeDataString(type.Name) + "/states?api-version=7.1", null, ct);
            resolvedTypes[index] = type with { States = states.RootElement.GetProperty("value").EnumerateArray().Select(s => new StateDefinition(Text(s, "name"), Text(s, "category", "Unknown"))).ToArray() };
        });
        types = resolvedTypes;
        using var query = await SendAsync(HttpMethod.Post, $"_apis/wit/wiql?$top={settings.MaxWorkItems}&api-version=7.1",
            JsonContent.Create(new { query = "Select [System.Id] From WorkItems Where [System.TeamProject] = @project And [System.State] <> 'Removed' Order By [System.ChangedDate] Desc" }), token);
        var ids = query.RootElement.GetProperty("workItems").EnumerateArray().Select(x => x.GetProperty("id").GetInt32()).ToArray();
        var items = new List<WorkItem>();
        foreach (var batch in ids.Chunk(200))
        {
            // Optional fields differ by process; requesting all fields avoids invalid field names.
            using var doc = await SendAsync(HttpMethod.Post, "_apis/wit/workitemsbatch?api-version=7.1", JsonContent.Create(new { ids = batch, errorPolicy = "Fail" }), token);
            items.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseItem));
        }
        var dashboards = new List<DashboardInfo>();
        string? warning = null;
        try
        {
            var team = string.IsNullOrWhiteSpace(settings.Team) ? "" : Uri.EscapeDataString(settings.Team.Trim()) + "/";
            using var doc = await SendAsync(HttpMethod.Get, team + "_apis/dashboard/dashboards?api-version=7.1-preview.3", null, token);
            foreach (var d in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = Text(d, "id");
                dashboards.Add(new(id, Text(d, "name"), Text(d, "description"), new Uri(client.BaseAddress!, team + "_dashboards/dashboard/" + Uri.EscapeDataString(id)).AbsoluteUri));
            }
        }
        catch (HttpRequestException ex) { warning = "Work items connected. Dashboards unavailable: " + ex.Message; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { warning = "Work items connected. Dashboard discovery timed out."; }
        return new(items.OrderByDescending(i => i.ChangedAt).ToArray(), types, dashboards, warning, ids.Length >= settings.MaxWorkItems);
    }
    public async Task<WorkItem> UpdateAsync(WorkItem item, WorkItemEdit edit, CancellationToken token)
    {
        ValidateTitle(edit.Title);
        if (edit.Priority is < 1 or > 4) throw new ArgumentException("Priority must be 1 to 4.");
        var operations = new List<object> { new { op = "test", path = "/rev", value = (object)item.Revision }, new { op = "add", path = "/fields/System.Title", value = (object)edit.Title.Trim() }, new { op = "add", path = "/fields/System.State", value = (object)edit.State }, new { op = "add", path = "/fields/System.Tags", value = (object)edit.Tags } };
        if (edit.Priority != item.Priority) operations.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.Priority", value = (object)edit.Priority });
        if (edit.Description != null)
        {
            ValidateRichText(edit.Description, "Description");
            operations.Add(new { op = "add", path = "/fields/" + item.DescriptionField, value = (object)edit.Description });
        }
        if (edit.AcceptanceCriteria != null)
        {
            ValidateRichText(edit.AcceptanceCriteria, "Acceptance criteria");
            operations.Add(new { op = "add", path = "/fields/Microsoft.VSTS.Common.AcceptanceCriteria", value = (object)edit.AcceptanceCriteria });
        }
        using var doc = await SendAsync(HttpMethod.Patch, $"_apis/wit/workitems/{item.Id}?api-version=7.1", Patch(operations.ToArray()), token);
        return ParseItem(doc.RootElement);
    }
    public async Task<WorkItem> CreateAsync(string type, string title, CancellationToken token)
    {
        ValidateTitle(title);
        using var doc = await SendAsync(HttpMethod.Post, "_apis/wit/workitems/$" + Uri.EscapeDataString(type) + "?api-version=7.1",
            Patch([new { op = "add", path = "/fields/System.Title", value = (object)title.Trim() }]), token);
        return ParseItem(doc.RootElement);
    }
    private static void ValidateTitle(string title)
    { if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 255) throw new ArgumentException("Enter a title between 1 and 255 characters."); }
    private static void ValidateRichText(string text, string name)
    { if (text.Length > 1_000_000) throw new ArgumentException(name + " is too large to save."); }
    private static StringContent Patch(object[] operations) => new(JsonSerializer.Serialize(operations), Encoding.UTF8, "application/json-patch+json");
    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            // Server response bodies and headers may contain sensitive data; never display them.
            var message = response.StatusCode switch {
                HttpStatusCode.Unauthorized => "Authentication failed. Check the PAT and its expiry.",
                HttpStatusCode.Forbidden => "Access denied. Check project permissions and PAT scopes.",
                HttpStatusCode.NotFound => "Project or resource not found. Check the project URL and team.",
                HttpStatusCode.BadRequest => "Azure DevOps rejected the request. Refresh and check fields, revision, and workflow rules.",
                HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => "This work item changed on the server. Refresh before saving again.",
                HttpStatusCode.TooManyRequests => "Azure DevOps is throttling requests. Wait a moment and refresh.",
                _ => $"Azure DevOps returned HTTP {(int)response.StatusCode}. Try refreshing." };
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
    }
    private WorkItem ParseItem(JsonElement element)
    {
        var fields = element.GetProperty("fields");
        var type = Text(fields, "System.WorkItemType", "Task");
        var state = Text(fields, "System.State");
        var assigned = fields.TryGetProperty("System.AssignedTo", out var a) ? a.ValueKind == JsonValueKind.Object ? Text(a, "displayName", "Unassigned") : a.GetString() ?? "Unassigned" : "Unassigned";
        var systemDescription = Text(fields, "System.Description");
        var reproductionSteps = Text(fields, "Microsoft.VSTS.TCM.ReproSteps");
        return new() { Id = element.GetProperty("id").GetInt32(), Revision = element.GetProperty("rev").GetInt32(),
            Title = Text(fields, "System.Title"), Type = type, State = state, AssignedTo = assigned,
            Iteration = Text(fields, "System.IterationPath"), Tags = Text(fields, "System.Tags"), Description = systemDescription.Length > 0 ? systemDescription : reproductionSteps,
            DescriptionField = systemDescription.Length > 0 || reproductionSteps.Length == 0 ? "System.Description" : "Microsoft.VSTS.TCM.ReproSteps",
            AcceptanceCriteria = Text(fields, "Microsoft.VSTS.Common.AcceptanceCriteria"),
            Priority = (int)Number(fields, "Microsoft.VSTS.Common.Priority", 2), Points = Number(fields, "Microsoft.VSTS.Scheduling.StoryPoints", Number(fields, "Microsoft.VSTS.Scheduling.Effort")),
            ChangedAt = DateTimeOffset.TryParse(Text(fields, "System.ChangedDate"), out var date) ? date : DateTimeOffset.MinValue,
            Category = types.FirstOrDefault(t => t.Name == type)?.States.FirstOrDefault(s => s.Name == state)?.Category ?? "Unknown" };
    }
    private static string Text(JsonElement e, string key, string fallback = "") => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static double Number(JsonElement e, string key, double fallback = 0) => e.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) ? n : fallback;
    public void Dispose() => client.Dispose();
}





