namespace DevOpsClient.Core;
public sealed partial class DemoProjectService
{
    public Task<WorkQueue> LoadAssignedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkQueue(items.Where(i => i.AssignedTo == "Alex Morgan").OrderByDescending(i => i.ChangedAt).ToArray()));
    }
    public Task<WorkItemDetails> GetDetailsAsync(int id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var item = items.FirstOrDefault(i => i.Id == id) ?? throw new ArgumentException("That work item does not exist in the demo workspace.");
        if (id == 1041) item = item with { Description = "<h2>Problem</h2><p>Concurrent requests can start a token refresh at the same time. When the first refresh succeeds, the remaining requests may still retry with the expired token.</p><h2>Implementation</h2><p>Serialize refreshes with a <code>SemaphoreSlim</code> and recheck token expiry after taking the lock.</p><pre>await refreshLock.WaitAsync(cancellationToken);\ntry {\n    await RefreshIfExpiredAsync();\n} finally {\n    refreshLock.Release();\n}</pre><h2>Acceptance criteria</h2><ul><li>Only one refresh runs per connection.</li><li>Waiting requests support cancellation.</li><li>A failed refresh surfaces an authentication error.</li></ul>" };
        (int Parent, int Child)[] hierarchy = [(1050, 1041), (1050, 1042), (1041, 1046), (1041, 1051)];
        var relations = hierarchy.Where(h => h.Child == id).Select(h => new RelatedWorkItem(h.Parent, "Parent", items.Single(i => i.Id == h.Parent)))
            .Concat(hierarchy.Where(h => h.Parent == id).Select(h => new RelatedWorkItem(h.Child, "Child", items.Single(i => i.Id == h.Child)))).ToList();
        if (id is 1041 or 1043) { var other = id == 1041 ? 1043 : 1041; relations.Add(new(other, "Related", items.Single(i => i.Id == other))); }
        return Task.FromResult(new WorkItemDetails(item, relations));
    }
    public Task<CommentPage> GetCommentsAsync(int id, string? continuationToken, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        WorkItemComment[] comments = [
            new(1, "Jordan Lee", DateTimeOffset.Now.AddDays(-2), "<p>The parent story covers connection resilience. Keep the refresh lock scoped to the connection, so switching projects does not block another workspace.</p>", true),
            new(2, "Alex Morgan", DateTimeOffset.Now.AddDays(-1), "I have the **cancellation** path in place. Adding coverage for `401` responses while a refresh is already in progress.\n\n- First refresh owns the lock\n- Waiting requests remain cancellable", false),
            new(3, "Sam Rivera", DateTimeOffset.Now.AddHours(-2), "<p>Please include a test where the first refresh fails and a waiting request retries. The failure should not leave the semaphore held.</p>", true)];
        var offset = continuationToken == "older-demo-page" ? 2 : 0;
        return Task.FromResult(new CommentPage(comments.Skip(offset).Take(2).ToArray(), offset == 0 ? "older-demo-page" : null, comments.Length));
    }
}

