namespace DevOpsClient.Core;
public sealed partial class DemoProjectService : IProjectService
{
    private readonly List<WorkItem> items = [];
    private readonly Dictionary<int, List<WorkItemComment>> comments = [];
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
        items[0] = items[0] with { Description = "<h2>Problem</h2><p>Concurrent requests can start a token refresh at the same time. When the first refresh succeeds, the remaining requests may still retry with the expired token.</p><h2>Implementation</h2><p>Serialize refreshes with a <code>SemaphoreSlim</code> and recheck token expiry after taking the lock.</p><pre>await refreshLock.WaitAsync(cancellationToken);\ntry {\n    await RefreshIfExpiredAsync();\n} finally {\n    refreshLock.Release();\n}</pre><h2>Acceptance criteria</h2><ul><li>Only one refresh runs per connection.</li><li>Waiting requests support cancellation.</li><li>A failed refresh surfaces an authentication error.</li></ul>" };
        comments[1041] =
        [
            new(1, "Jordan Lee", DateTimeOffset.Now.AddDays(-2), "<p>The parent story covers connection resilience. Keep the refresh lock scoped to the connection, so switching projects does not block another workspace.</p>", true, "<p>The parent story covers connection resilience. Keep the refresh lock scoped to the connection, so switching projects does not block another workspace.</p>"),
            new(2, "Alex Morgan", DateTimeOffset.Now.AddDays(-1), "I have the **cancellation** path in place. Adding coverage for `401` responses while a refresh is already in progress.\n\n- First refresh owns the lock\n- Waiting requests remain cancellable", false, "I have the **cancellation** path in place. Adding coverage for `401` responses while a refresh is already in progress.\n\n- First refresh owns the lock\n- Waiting requests remain cancellable", true),
            new(3, "Sam Rivera", DateTimeOffset.Now.AddHours(-2), "<p>Please include a test where the first refresh fails and a waiting request retries. The failure should not leave the semaphore held.</p>", true, "<p>Please include a test where the first refresh fails and a waiting request retries. The failure should not leave the semaphore held.</p>")
        ];
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
            Description = edit.Description ?? item.Description, AcceptanceCriteria = edit.AcceptanceCriteria ?? item.AcceptanceCriteria,
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

