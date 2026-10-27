using System.Text.Json;
namespace DevOpsClient.Core;

public sealed record ClientSettings
{
    public string ProjectUrl { get; init; } = "";
    public string PersonalAccessToken { get; init; } = "";
    public string Team { get; init; } = "";
    public bool UseDemoData { get; init; } = true;
    public int MaxWorkItems { get; init; } = 500;
    public string ThemeFamily { get; init; } = "Fluent";
    public string Theme { get; init; } = "Dark";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontScale { get; init; } = 1.0;
    public Uri ProjectUri()
    {
        if (!Uri.TryCreate(ProjectUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Enter an HTTPS Azure DevOps project URL, without query parameters.");
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var valid = uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase) ? parts.Length == 2
            : uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && parts.Length == 1;
        if (!valid) throw new ArgumentException("Use https://dev.azure.com/organization/project (or https://organization.visualstudio.com/project).");
        return uri;
    }
    public void Validate()
    {
        if (MaxWorkItems is < 1 or > 2000) throw new ArgumentException("Work item limit must be between 1 and 2000.");
        if (ThemeFamily is not ("Fluent" or "Windows 11" or "Material" or "Office 2019")) throw new ArgumentException("Choose a supported Telerik theme family.");
        if (Theme is not ("Dark" or "Light" or "High contrast")) throw new ArgumentException("Choose a supported theme.");
        if (FontScale is < 0.8 or > 1.8) throw new ArgumentException("Font scale must be between 80% and 180%.");
        if (string.IsNullOrWhiteSpace(FontFamily)) throw new ArgumentException("Choose a font family.");
        if (UseDemoData) return;
        _ = ProjectUri();
        if (string.IsNullOrWhiteSpace(PersonalAccessToken)) throw new ArgumentException("Add your personal access token to connect.");
    }
    public string ProjectName => UseDemoData ? "Meridian" : Uri.UnescapeDataString(ProjectUri().Segments.Last().Trim('/'));
}
public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static ClientSettings Load(string folder)
    {
        var local = Path.Combine(folder, "appsettings.local.json");
        var path = File.Exists(local) ? local : Path.Combine(folder, "appsettings.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The settings file is empty.") : new();
    }
    public static void Save(string folder, ClientSettings settings)
    {
        settings.Validate();
        var path = Path.Combine(folder, "appsettings.local.json");
        var temp = path + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
public sealed record WorkItem
{
    public int Id { get; init; }
    public int Revision { get; init; }
    public string Title { get; init; } = "";
    public string Type { get; init; } = "Task";
    public string State { get; init; } = "New";
    public string AssignedTo { get; init; } = "Unassigned";
    public string Iteration { get; init; } = "";
    public string Tags { get; init; } = "";
    public string Description { get; init; } = "";
    public string DescriptionField { get; init; } = "System.Description";
    public string AcceptanceCriteria { get; init; } = "";
    public int Priority { get; init; } = 2;
    public double Points { get; init; }
    public DateTimeOffset ChangedAt { get; init; }
    public string Category { get; init; } = "Proposed";
    public string Initials => string.Concat(AssignedTo.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => s[0]));
    public string ShortIteration => Iteration.Split('\\').LastOrDefault() ?? "";
    public string KindColor => Type switch { "Bug" => "#F0808D", "Feature" => "#B69CFF", "Epic" => "#F1B35E", "Task" => "#6EC6EF", _ => "#70D6AF" };
}
public sealed record StateDefinition(string Name, string Category);
public sealed record WorkItemType(string Name, IReadOnlyList<StateDefinition> States);
public sealed record DashboardInfo(string Id, string Name, string Description, string Url);
public sealed record ProjectSnapshot(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemType> Types,
    IReadOnlyList<DashboardInfo> Dashboards, string? DashboardWarning = null, bool LimitReached = false);
public sealed record WorkItemEdit(string Title, string State, string Tags, int Priority, string? Description = null, string? AcceptanceCriteria = null);
public interface IProjectService : IDisposable
{
    Task<ProjectSnapshot> LoadAsync(CancellationToken token);
    Task<WorkQueue> LoadAssignedAsync(CancellationToken token);
    Task<WorkItemDetails> GetDetailsAsync(int id, CancellationToken token);
    Task<CommentPage> GetCommentsAsync(int id, string? continuationToken, CancellationToken token);
    Task<WorkItemComment> AddCommentAsync(int id, string text, CancellationToken token);
    Task<WorkItemComment> UpdateCommentAsync(int id, WorkItemComment comment, string text, CancellationToken token);
    Task<WorkItem> UpdateAsync(WorkItem item, WorkItemEdit edit, CancellationToken token);
    Task<WorkItem> CreateAsync(string type, string title, CancellationToken token);
}

public sealed record WorkQueue(IReadOnlyList<WorkItem> Items, bool LimitReached = false);
public sealed record RelatedWorkItem(int Id, string Relationship, WorkItem? Item);
public sealed record WorkItemDetails(WorkItem Item, IReadOnlyList<RelatedWorkItem> Relations, string? RelationsWarning = null);
public sealed record WorkItemComment(int Id, string Author, DateTimeOffset CreatedAt, string Text, bool IsHtml,
    string EditableText = "", bool IsMarkdown = false, int Version = 1);
public sealed record CommentPage(IReadOnlyList<WorkItemComment> Comments, string? ContinuationToken, int TotalCount);


