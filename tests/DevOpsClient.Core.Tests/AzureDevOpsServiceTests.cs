using System.Net;
using System.Text;
using System.Text.Json;
using DevOpsClient.Core;
using Xunit;

namespace DevOpsClient.Core.Tests;

public sealed class AzureDevOpsServiceTests
{
    [Fact]
    public async Task Load_batches_201_items_and_maps_custom_states()
    {
        var batches = new List<int>();
        using var handler = new FakeHandler(async request =>
        {
            Assert.StartsWith("/org/My%20Project/", request.RequestUri!.AbsolutePath);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(":test-token")), request.Headers.Authorization?.Parameter);
            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("workitemtypes")) return Json("{\"value\":[{\"name\":\"User Story\",\"states\":[{\"name\":\"Building\",\"category\":\"InProgress\"}]}]}");
            if (path.EndsWith("wiql"))
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Contains("$top=500", request.RequestUri.Query);
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("@project", body);
                return Json(JsonSerializer.Serialize(new { workItems = Enumerable.Range(1, 201).Select(id => new { id }) }));
            }
            if (path.EndsWith("workitemsbatch"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var ids = body.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                batches.Add(ids.Length);
                return Json(JsonSerializer.Serialize(new { value = ids.Select(id => new { id, rev = 3, fields = new Dictionary<string, object> { ["System.Title"] = "Item " + id, ["System.WorkItemType"] = "User Story", ["System.State"] = "Building", ["System.AssignedTo"] = new { displayName = "Ada Lovelace" }, ["Microsoft.VSTS.Scheduling.Effort"] = 5 } }) }));
            }
            return Json("{\"value\":[{\"id\":\"abc\",\"name\":\"Team view\",\"description\":\"Overview\"}]}");
        });

        using var client = new AzureDevOpsService(Live(), handler);
        var snapshot = await client.LoadAsync(default);

        Assert.Equal([200, 1], batches);
        Assert.Equal(201, snapshot.Items.Count);
        Assert.Equal("InProgress", snapshot.Items[0].Category);
        Assert.Equal("Ada Lovelace", snapshot.Items[0].AssignedTo);
        Assert.Equal(5, snapshot.Items[0].Points);
        Assert.False(snapshot.LimitReached);
        Assert.Equal("https://dev.azure.com/org/My%20Project/_dashboards/dashboard/abc", snapshot.Dashboards.Single().Url);
    }

    [Fact]
    public async Task Dashboard_permission_failure_preserves_work_items()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(request => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("dashboard")
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Json("{\"value\":[],\"workItems\":[]}"))));

        var result = await client.LoadAsync(default);

        Assert.Empty(result.Items);
        Assert.Contains("Access denied", result.DashboardWarning);
    }

    [Fact]
    public async Task Update_sends_revision_guard_and_json_patch_fields()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(async request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("application/json-patch+json", request.Content!.Headers.ContentType!.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync());
            Assert.Equal("test", body.RootElement[0].GetProperty("op").GetString());
            Assert.Equal("/rev", body.RootElement[0].GetProperty("path").GetString());
            Assert.Equal(7, body.RootElement[0].GetProperty("value").GetInt32());
            Assert.Equal("Updated", body.RootElement[1].GetProperty("value").GetString());
            return Json("{\"id\":42,\"rev\":8,\"fields\":{\"System.Title\":\"Updated\"}}");
        }));

        var item = await client.UpdateAsync(new() { Id = 42, Revision = 7 }, new(" Updated ", "New", "desktop", 1), default);

        Assert.Equal(8, item.Revision);
    }

    [Fact]
    public async Task Content_update_uses_the_original_description_field_and_revision_guard()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(async request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var operations = body.RootElement.EnumerateArray().ToArray();
            Assert.Contains(operations, operation => operation.GetProperty("path").GetString() == "/rev" && operation.GetProperty("value").GetInt32() == 9);
            Assert.Contains(operations, operation => operation.GetProperty("path").GetString() == "/fields/Microsoft.VSTS.TCM.ReproSteps" && operation.GetProperty("value").GetString() == "<p>Edited</p>");
            Assert.Contains(operations, operation => operation.GetProperty("path").GetString() == "/fields/Microsoft.VSTS.Common.AcceptanceCriteria" && operation.GetProperty("value").GetString() == "<p>Verified</p>");
            return Json("{\"id\":42,\"rev\":10,\"fields\":{\"System.Title\":\"Bug\",\"Microsoft.VSTS.TCM.ReproSteps\":\"<p>Edited</p>\",\"Microsoft.VSTS.Common.AcceptanceCriteria\":\"<p>Verified</p>\"}}");
        }));

        var item = new WorkItem { Id = 42, Revision = 9, Title = "Bug", State = "New", DescriptionField = "Microsoft.VSTS.TCM.ReproSteps" };
        var updated = await client.UpdateAsync(item, new("Bug", "New", "", 2, Description: "<p>Edited</p>", AcceptanceCriteria: "<p>Verified</p>"), default);

        Assert.Equal("<p>Edited</p>", updated.Description);
        Assert.Equal("<p>Verified</p>", updated.AcceptanceCriteria);
    }

    [Fact]
    public async Task Comment_writes_use_markdown_and_preserve_the_editable_source()
    {
        var calls = 0;
        using var client = new AzureDevOpsService(Live(), new FakeHandler(async request =>
        {
            calls++;
            Assert.Contains("format=markdown", request.RequestUri!.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (calls == 1)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/workitems/42/comments", request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("New **comment**", body.RootElement.GetProperty("text").GetString());
                return Json("{\"id\":51,\"version\":1,\"text\":\"New **comment**\",\"format\":\"markdown\",\"renderedText\":\"<p>New <strong>comment</strong></p>\",\"createdBy\":{\"displayName\":\"Ada\"},\"createdDate\":\"2026-10-24T10:00:00Z\"}");
            }
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.EndsWith("/workitems/42/comments/51", request.RequestUri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Edited **comment**", body.RootElement.GetProperty("text").GetString());
            return Json("{\"id\":51,\"version\":2,\"text\":\"Edited **comment**\",\"format\":\"markdown\",\"renderedText\":\"<p>Edited <strong>comment</strong></p>\",\"createdBy\":{\"displayName\":\"Ada\"},\"createdDate\":\"2026-10-24T10:00:00Z\"}");
        }));

        var added = await client.AddCommentAsync(42, "New **comment**", default);
        var edited = await client.UpdateCommentAsync(42, added, "Edited **comment**", default);

        Assert.Equal("New **comment**", added.EditableText);
        Assert.True(added.IsMarkdown);
        Assert.Equal("Edited **comment**", edited.EditableText);
        Assert.Equal(2, edited.Version);
    }

    [Fact]
    public async Task Create_encodes_type_and_sends_title()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/$User%20Story", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("/fields/System.Title", body);
            return Json("{\"id\":43,\"rev\":1,\"fields\":{\"System.Title\":\"Created\"}}");
        }));

        var item = await client.CreateAsync("User Story", "Created", default);

        Assert.Equal(43, item.Id);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Api_failures_are_sanitized(HttpStatusCode status)
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("secret-server-body test-token") })));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.LoadAsync(default));

        Assert.Equal(status, exception.StatusCode);
        Assert.DoesNotContain("secret-server-body", exception.Message);
        Assert.DoesNotContain("test-token", exception.Message);
    }

    [Fact]
    public async Task Cancellation_stops_api_operations()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new AzureDevOpsService(Live(), new FakeHandler(_ => throw new Exception("Should not send")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.LoadAsync(cancellation.Token));
    }

    [Fact]
    public async Task Missing_type_state_metadata_is_fetched_from_states_endpoint()
    {
        var statesCalled = false;
        using var client = new AzureDevOpsService(Live(), new FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("workitemtypes")) return Task.FromResult(Json("{\"value\":[{\"name\":\"Task\"}]}") );
            if (path.EndsWith("/Task/states")) { statesCalled = true; return Task.FromResult(Json("{\"value\":[{\"name\":\"Doing\",\"category\":\"InProgress\"}]}") ); }
            return Task.FromResult(Json("{\"value\":[],\"workItems\":[]}"));
        }));

        var result = await client.LoadAsync(default);

        Assert.True(statesCalled);
        Assert.Equal("InProgress", result.Types.Single().States.Single().Category);
    }

    [Fact]
    public async Task Invalid_edits_are_rejected_before_http()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(_ => throw new Exception("Must not send")));

        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateAsync("Task", " ", default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateAsync(new(), new("Valid", "New", "", 0), default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddCommentAsync(42, " ", default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateCommentAsync(42, new(1, "Ada", DateTimeOffset.Now, "", false), " ", default));
    }

    [Fact]
    public async Task Assigned_queue_uses_server_side_me_query()
    {
        var requested = false;
        using var client = new AzureDevOpsService(Live() with { MaxWorkItems = 1 }, new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("wiql"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var wiql = body.RootElement.GetProperty("query").GetString()!;
                Assert.Contains("[System.AssignedTo] = @Me", wiql);
                Assert.Contains("@project", wiql);
                Assert.Contains("$top=1", request.RequestUri.Query);
                requested = true;
                return Json("{\"workItems\":[{\"id\":7001}]}");
            }
            return Json("{\"value\":[{\"id\":7001,\"rev\":1,\"fields\":{\"System.Title\":\"An older assigned item\"}}]}");
        }));

        var result = await client.LoadAssignedAsync(default);

        Assert.True(requested);
        Assert.Equal(7001, result.Items.Single().Id);
        Assert.True(result.LimitReached);
    }

    [Fact]
    public async Task Detail_relationships_use_trusted_ids_and_retain_inaccessible_relatives()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Contains("$expand=relations", request.RequestUri!.Query);
                return Json("{\"id\":42,\"rev\":3,\"fields\":{\"System.Title\":\"Item\",\"Microsoft.VSTS.TCM.ReproSteps\":\"<p>Reproduce the failure</p>\",\"Microsoft.VSTS.Common.AcceptanceCriteria\":\"<p>It works</p>\"},\"relations\":[{\"rel\":\"System.LinkTypes.Hierarchy-Reverse\",\"url\":\"https://dev.azure.com/org/_apis/wit/workItems/40\"},{\"rel\":\"System.LinkTypes.Hierarchy-Forward\",\"url\":\"https://dev.azure.com/org/OtherProject/_apis/wit/workItems/41\"},{\"rel\":\"System.LinkTypes.Related\",\"url\":\"https://dev.azure.com/other-org/_apis/wit/workItems/99\"},{\"rel\":\"System.LinkTypes.Related\",\"url\":\"https://evil.example/_apis/wit/workItems/98\"},{\"rel\":\"Hyperlink\",\"url\":\"https://example.com\"}]}");
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal([40, 41], body.RootElement.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()));
            Assert.Equal("Omit", body.RootElement.GetProperty("errorPolicy").GetString());
            return Json("{\"value\":[{\"id\":40,\"rev\":1,\"fields\":{\"System.Title\":\"Parent outside the queue\"}}]}");
        }));

        var result = await client.GetDetailsAsync(42, default);

        Assert.Contains("Reproduce", result.Item.Description);
        Assert.Contains("It works", result.Item.AcceptanceCriteria);
        Assert.Equal(2, result.Relations.Count);
        Assert.Equal("Parent", result.Relations[0].Relationship);
        Assert.Equal("Parent outside the queue", result.Relations[0].Item!.Title);
        Assert.Equal("Child", result.Relations[1].Relationship);
        Assert.Null(result.Relations[1].Item);
        Assert.NotNull(result.RelationsWarning);
    }

    [Fact]
    public async Task Linked_item_failure_does_not_hide_description()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Json("{\"id\":42,\"rev\":1,\"fields\":{\"System.Title\":\"Still readable\",\"System.Description\":\"Keep this description\"},\"relations\":[{\"rel\":\"System.LinkTypes.Related\",\"url\":\"https://dev.azure.com/org/_apis/wit/workItems/41\"}]}")
            : new HttpResponseMessage(HttpStatusCode.Forbidden))));

        var result = await client.GetDetailsAsync(42, default);

        Assert.Equal("Keep this description", result.Item.Description);
        Assert.NotNull(result.RelationsWarning);
    }

    [Fact]
    public async Task Comments_use_continuation_tokens_rendered_text_and_hide_deleted_entries()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(request =>
        {
            Assert.Contains("continuationToken=a%2Bb%2Fc%3D", request.RequestUri!.Query);
            Assert.Contains("7.1-preview.4", request.RequestUri.Query);
            return Task.FromResult(Json("{\"totalCount\":3,\"continuationToken\":\"next\",\"comments\":[{\"id\":1,\"text\":\"raw\",\"renderedText\":\"<p>Readable</p>\",\"createdBy\":{\"displayName\":\"Ada\"},\"createdDate\":\"2026-10-01T08:00:00Z\"},{\"commentId\":2,\"text\":\"Plain Markdown **text**\",\"format\":\"markdown\"},{\"id\":3,\"text\":\"Deleted\",\"isDeleted\":true}]}"));
        }));

        var result = await client.GetCommentsAsync(42, "a+b/c=", default);

        Assert.Equal(2, result.Comments.Count);
        Assert.Equal("<p>Readable</p>", result.Comments[0].Text);
        Assert.True(result.Comments[0].IsHtml);
        Assert.Equal("Ada", result.Comments[0].Author);
        Assert.Equal("raw", result.Comments[0].EditableText);
        Assert.False(result.Comments[1].IsHtml);
        Assert.True(result.Comments[1].IsMarkdown);
        Assert.Equal(2, result.Comments[1].Id);
        Assert.Equal("next", result.ContinuationToken);
        Assert.Equal(3, result.TotalCount);
    }

    [Fact]
    public async Task Reading_endpoints_reject_invalid_ids_before_http()
    {
        using var client = new AzureDevOpsService(Live(), new FakeHandler(_ => throw new Exception("Must not send")));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetDetailsAsync(0, default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCommentsAsync(-1, null, default));
    }

    private static ClientSettings Live() => new() { ProjectUrl = "https://dev.azure.com/org/My%20Project", PersonalAccessToken = "test-token", UseDemoData = false };
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return response(request);
        }
    }
}
