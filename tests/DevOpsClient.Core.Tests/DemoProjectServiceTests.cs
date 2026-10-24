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
}
