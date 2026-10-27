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
        (int Parent, int Child)[] hierarchy = [(1050, 1041), (1050, 1042), (1041, 1046), (1041, 1051)];
        var relations = hierarchy.Where(h => h.Child == id).Select(h => new RelatedWorkItem(h.Parent, "Parent", items.Single(i => i.Id == h.Parent)))
            .Concat(hierarchy.Where(h => h.Parent == id).Select(h => new RelatedWorkItem(h.Child, "Child", items.Single(i => i.Id == h.Child)))).ToList();
        if (id is 1041 or 1043) { var other = id == 1041 ? 1043 : 1041; relations.Add(new(other, "Related", items.Single(i => i.Id == other))); }
        return Task.FromResult(new WorkItemDetails(item, relations));
    }
    public Task<CommentPage> GetCommentsAsync(int id, string? continuationToken, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var discussion = comments.GetValueOrDefault(id) ?? [];
        var offset = continuationToken == "older-demo-page" ? 2 : 0;
        return Task.FromResult(new CommentPage(discussion.Skip(offset).Take(2).ToArray(), offset == 0 && discussion.Count > 2 ? "older-demo-page" : null, discussion.Count));
    }

    public Task<WorkItemComment> AddCommentAsync(int id, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (id <= 0 || items.All(item => item.Id != id)) throw new ArgumentException("That work item does not exist in the demo workspace.");
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a comment before saving.");
        if (!comments.TryGetValue(id, out var discussion)) comments[id] = discussion = [];
        var nextId = comments.Values.SelectMany(value => value).Select(comment => comment.Id).DefaultIfEmpty().Max() + 1;
        var comment = new WorkItemComment(nextId, "Alex Morgan", DateTimeOffset.Now, text, false, text, true);
        discussion.Add(comment);
        return Task.FromResult(comment);
    }

    public Task<WorkItemComment> UpdateCommentAsync(int id, WorkItemComment comment, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Enter a comment before saving.");
        if (!comments.TryGetValue(id, out var discussion)) throw new ArgumentException("That discussion does not exist in the demo workspace.");
        var index = discussion.FindIndex(existing => existing.Id == comment.Id);
        if (index < 0) throw new ArgumentException("That comment does not exist in the demo workspace.");
        var updated = comment with { Text = text, EditableText = text, IsHtml = !comment.IsMarkdown && comment.IsHtml, Version = comment.Version + 1 };
        discussion[index] = updated;
        return Task.FromResult(updated);
    }
}

