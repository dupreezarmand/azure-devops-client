namespace DevOpsClient.Core;
public sealed partial class DemoProjectService : IProjectService
{
    private readonly List<WorkItem> items = [];
    private readonly WorkItemType[] types = [new("User Story", States), new("Bug", States), new("Task", States), new("Feature", States)];
    private static readonly StateDefinition[] States = [new("New", "Proposed"), new("Active", "InProgress"), new("Resolved", "Resolved"), new("Closed", "Completed")];
    public DemoProjectService()
    {
        string[] titles = ["Handle token refresh during concurrent requests", "Add cancellation to the work item loader", "Fix notification badge overflow", "Design the release readiness view", "Add keyboard navigation to the board", "Reduce query response time", "Polish empty states and loading feedback", "Review accessibility contrast", "Export work items to a portable format", "Restore filters between sessions", "Fix sprint date timezone handling", "Ship the onboarding checklist", "Add connection health diagnostics", "Document the deployment workflow", "Improve retry behavior on throttling", "Validate work item state transitions", "Build the team activity view", "Align the command bar spacing", "Resolve duplicate task notifications", "Finalize the release notes"];
        string[] names = ["Alex Morgan", "Jordan Lee", "Sam Rivera", "Taylor Chen", "Unassigned"];
        for (var i = 0; i < titles.Length; i++)
        {
            var state = States[i % States.Length];
            items.Add(new() { Id = 1041 + i, Revision = 1, Title = titles[i], Type = i == 0 ? "Task" : types[(i / 3) % types.Length].Name,
                State = state.Name, Category = state.Category, AssignedTo = names[i % names.Length], Priority = i % 3 + 1,
                Points = new[] { 3, 5, 2, 8 }[i % 4], Iteration = "Meridian\\Sprint 24", Tags = i % 3 == 0 ? "desktop; experience" : "platform",
                ChangedAt = DateTimeOffset.Now.AddHours(-i * 3), Description = "Make the everyday workflow feel focused, fast, and considered.\n\nAcceptance criteria\n• The experience works with keyboard navigation.\n• Empty and error states are clear.\n• The team can verify the result end to end." });
        }
    }
    public Task<ProjectSnapshot> LoadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new ProjectSnapshot(items.ToArray(), types,
            [new("demo", "Team delivery", "A shared view of the work that matters. Connect to see your Azure DevOps dashboards.", "")]));
    }
    public Task<WorkItem> UpdateAsync(WorkItem item, WorkItemEdit edit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var updated = item with { Title = edit.Title.Trim(), State = edit.State, Tags = edit.Tags, Priority = edit.Priority,
            Revision = item.Revision + 1, ChangedAt = DateTimeOffset.Now, Category = States.First(s => s.Name == edit.State).Category };
        items[items.FindIndex(x => x.Id == item.Id)] = updated;
        return Task.FromResult(updated);
    }
    public Task<WorkItem> CreateAsync(string type, string title, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var item = new WorkItem { Id = items.Max(x => x.Id) + 1, Revision = 1, Title = title.Trim(), Type = type, ChangedAt = DateTimeOffset.Now, Iteration = "Meridian\\Sprint 24" };
        items.Add(item);
        return Task.FromResult(item);
    }
    public void Dispose() { }
}

