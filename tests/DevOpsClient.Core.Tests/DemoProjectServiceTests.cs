using DevOpsClient.Core;
using Xunit;

namespace DevOpsClient.Core.Tests;

public sealed class DemoProjectServiceTests
{
    [Fact]
    public async Task Create_edit_and_refresh_retain_session_changes()
    {
        using var demo = new DemoProjectService();
        var before = await demo.LoadAsync(default);
        var created = await demo.CreateAsync("Bug", "My bug", default);

        var updated = await demo.UpdateAsync(created, new("Fixed bug", "Closed", "verified", 1), default);
        var after = await demo.LoadAsync(default);

        Assert.Equal(before.Items.Count + 1, after.Items.Count);
        Assert.Equal("Fixed bug", after.Items.Single(x => x.Id == created.Id).Title);
        Assert.Equal("Completed", updated.Category);
        Assert.Equal(2, updated.Revision);
    }

    [Fact]
    public async Task Assigned_queue_relationships_and_paged_discussion_are_available()
    {
        using var demo = new DemoProjectService();
        var queue = await demo.LoadAssignedAsync(default);
        var detail = await demo.GetDetailsAsync(1041, default);
        var parent = detail.Relations.Single(r => r.Relationship == "Parent");

        Assert.All(queue.Items, item => Assert.Equal("Alex Morgan", item.AssignedTo));
        Assert.Contains("SemaphoreSlim", detail.Item.Description);
        Assert.Contains((await demo.GetDetailsAsync(parent.Id, default)).Relations, relation => relation.Relationship == "Child" && relation.Id == 1041);

        var first = await demo.GetCommentsAsync(1041, null, default);
        var second = await demo.GetCommentsAsync(1041, first.ContinuationToken, default);
        Assert.Equal(3, first.Comments.Count + second.Comments.Count);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task Description_and_comment_edits_persist_for_the_demo_session()
    {
        using var demo = new DemoProjectService();
        var details = await demo.GetDetailsAsync(1041, default);

        var updated = await demo.UpdateAsync(details.Item,
            new(details.Item.Title, details.Item.State, details.Item.Tags, details.Item.Priority,
                Description: "# Updated description", AcceptanceCriteria: "- Verified"), default);
        var added = await demo.AddCommentAsync(1041, "New **Markdown** comment", default);
        var edited = await demo.UpdateCommentAsync(1041, added, "Edited **Markdown** comment", default);

        var refreshed = await demo.GetDetailsAsync(1041, default);
        var comments = await demo.GetCommentsAsync(1041, null, default);
        var remaining = await demo.GetCommentsAsync(1041, comments.ContinuationToken, default);
        Assert.Equal("# Updated description", refreshed.Item.Description);
        Assert.Equal("- Verified", refreshed.Item.AcceptanceCriteria);
        Assert.Equal(2, updated.Revision);
        Assert.Contains(comments.Comments.Concat(remaining.Comments), comment => comment.Id == edited.Id && comment.EditableText == "Edited **Markdown** comment");
    }
}
