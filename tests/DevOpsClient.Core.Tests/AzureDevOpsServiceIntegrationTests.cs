using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevOpsClient.Core;
using Xunit;

namespace DevOpsClient.Core.Tests;

public sealed class AzureDevOpsServiceIntegrationTests
{
    [Fact]
    public async Task Load_async_uses_real_http_pipeline_for_types_query_batch_and_dashboards()
    {
        await using var server = await LoopbackServer.Start(request =>
        {
            if (request.Target.Contains("workitemtypes", StringComparison.Ordinal)) return JsonResponse("{\"value\":[{\"name\":\"Task\",\"states\":[{\"name\":\"Active\",\"category\":\"InProgress\"}]}]}");
            if (request.Target.Contains("/wiql?", StringComparison.Ordinal)) return JsonResponse("{\"workItems\":[{\"id\":101},{\"id\":102}]}");
            if (request.Target.Contains("workitemsbatch", StringComparison.Ordinal)) return JsonResponse(JsonSerializer.Serialize(new { value = new[] { Item(101), Item(102) } }));
            if (request.Target.Contains("dashboard", StringComparison.Ordinal)) return JsonResponse("{\"value\":[{\"id\":\"dash-1\",\"name\":\"Team view\",\"description\":\"Overview\"}]}");
            return NotFound();
        });

        using var service = new AzureDevOpsService(Live(), endpointOverride: server.BaseUri);
        var snapshot = await service.LoadAsync(default);

        Assert.Equal([101, 102], snapshot.Items.Select(i => i.Id));
        Assert.Equal("InProgress", snapshot.Items[0].Category);
        Assert.Single(snapshot.Dashboards);
        Assert.All(server.Requests, request => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":integration-token")), request.Headers["Authorization"]));
        Assert.Contains(server.Requests, request => request.Target.Contains("workitemsbatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assigned_work_and_comments_use_real_http_paging_and_encoding()
    {
        await using var server = await LoopbackServer.Start(request =>
        {
            if (request.Target.Contains("/wiql?", StringComparison.Ordinal)) return JsonResponse("{\"workItems\":[{\"id\":7001}]}");
            if (request.Target.Contains("workitemsbatch", StringComparison.Ordinal)) return JsonResponse(JsonSerializer.Serialize(new { value = new[] { Item(7001) } }));
            if (request.Target.Contains("comments", StringComparison.Ordinal) && request.Target.Contains("continuationToken=next", StringComparison.Ordinal)) return JsonResponse("{\"totalCount\":2,\"comments\":[{\"id\":2,\"text\":\"Second\",\"format\":\"markdown\"}]}");
            if (request.Target.Contains("comments", StringComparison.Ordinal)) return JsonResponse("{\"totalCount\":2,\"continuationToken\":\"next\",\"comments\":[{\"id\":1,\"text\":\"First\",\"format\":\"markdown\"}]}");
            return NotFound();
        });

        using var service = new AzureDevOpsService(Live(), endpointOverride: server.BaseUri);
        var queue = await service.LoadAssignedAsync(default);
        var first = await service.GetCommentsAsync(7001, "a+b/c=", default);
        var second = await service.GetCommentsAsync(7001, first.ContinuationToken, default);

        Assert.Equal(7001, queue.Items.Single().Id);
        Assert.Equal("next", first.ContinuationToken);
        Assert.Equal(2, first.Comments.Count + second.Comments.Count);
        Assert.Contains(server.Requests, request => request.Target.Contains("continuationToken=a%2Bb%2Fc%3D", StringComparison.Ordinal));
        Assert.Contains(server.Requests, request => request.Target.Contains("continuationToken=next", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_and_update_use_real_http_methods_content_types_and_revision_bodies()
    {
        await using var server = await LoopbackServer.Start(request =>
        {
            if (request.Method == "POST" && request.Target.Contains("workitems/$", StringComparison.Ordinal)) return JsonResponse(JsonSerializer.Serialize(Item(8001, "Created")));
            if (request.Method == "PATCH" && request.Target.Contains("workitems/8001", StringComparison.Ordinal)) return JsonResponse(JsonSerializer.Serialize(Item(8001, "Updated", 2)));
            return NotFound();
        });

        using var service = new AzureDevOpsService(Live(), endpointOverride: server.BaseUri);
        var created = await service.CreateAsync("User Story", "Created", default);
        var updated = await service.UpdateAsync(created, new("Updated", "Active", "integration", 1), default);

        var createRequest = Assert.Single(server.Requests, request => request.Method == "POST");
        var updateRequest = Assert.Single(server.Requests, request => request.Method == "PATCH");
        Assert.StartsWith("application/json-patch+json", createRequest.Headers["Content-Type"]);
        Assert.StartsWith("application/json-patch+json", updateRequest.Headers["Content-Type"]);
        Assert.Contains("System.Title", createRequest.Body);
        Assert.Contains("\"/rev\"", updateRequest.Body);
        Assert.Equal(2, updated.Revision);
    }

    [Fact]
    public async Task Comment_create_and_edit_use_real_http_pipeline_and_markdown_format()
    {
        await using var server = await LoopbackServer.Start(request =>
        {
            if (request.Method == "POST" && request.Target.Contains("comments?", StringComparison.Ordinal))
                return JsonResponse("{\"id\":7,\"version\":1,\"text\":\"Created **comment**\",\"format\":\"markdown\"}");
            if (request.Method == "PATCH" && request.Target.Contains("comments/7?", StringComparison.Ordinal))
                return JsonResponse("{\"id\":7,\"version\":2,\"text\":\"Edited **comment**\",\"format\":\"markdown\"}");
            return NotFound();
        });

        using var service = new AzureDevOpsService(Live(), endpointOverride: server.BaseUri);
        var created = await service.AddCommentAsync(8001, "Created **comment**", default);
        var edited = await service.UpdateCommentAsync(8001, created, "Edited **comment**", default);

        var createRequest = Assert.Single(server.Requests, request => request.Method == "POST");
        var editRequest = Assert.Single(server.Requests, request => request.Method == "PATCH");
        Assert.Contains("format=markdown", createRequest.Target);
        Assert.Contains("format=markdown", editRequest.Target);
        Assert.Contains("Created **comment**", createRequest.Body);
        Assert.Contains("Edited **comment**", editRequest.Body);
        Assert.Equal(2, edited.Version);
    }

    private static ClientSettings Live() => new() { ProjectUrl = "https://dev.azure.com/org/My%20Project", PersonalAccessToken = "integration-token", UseDemoData = false };
    private static object Item(int id, string title = "Integration item", int revision = 1) => new { id, rev = revision, fields = new Dictionary<string, object> { ["System.Title"] = title, ["System.WorkItemType"] = "Task", ["System.State"] = "Active", ["System.AssignedTo"] = new { displayName = "Integration User" }, ["System.ChangedDate"] = "2026-10-24T10:00:00Z" } };
    private static WireResponse JsonResponse(string body) => new(200, "application/json", body);
    private static WireResponse NotFound() => new(404, "application/json", "{}");

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener;
        private readonly Func<WireRequest, WireResponse> responder;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task acceptLoop;
        private LoopbackServer(TcpListener listener, Func<WireRequest, WireResponse> responder)
        {
            this.listener = listener; this.responder = responder; Requests = new();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/org/My%20Project/");
            acceptLoop = AcceptAsync();
        }
        public Uri BaseUri { get; }
        public ConcurrentBag<WireRequest> Requests { get; }
        public static Task<LoopbackServer> Start(Func<WireRequest, WireResponse> responder)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            return Task.FromResult(new LoopbackServer(listener, responder));
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    _ = HandleAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var request = await ReadRequestAsync(stream, cancellation.Token);
                Requests.Add(request);
                var response = responder(request);
                var body = Encoding.UTF8.GetBytes(response.Body);
                var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Not Found")}\r\nContent-Type: {response.ContentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, cancellation.Token); await stream.WriteAsync(body, cancellation.Token); await stream.FlushAsync(cancellation.Token);
            }
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel(); listener.Stop();
            try { await acceptLoop; } catch (OperationCanceledException) { }
            cancellation.Dispose();
        }
        private static async Task<WireRequest> ReadRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var bytes = new List<byte>(); var buffer = new byte[4096]; var headerEnd = -1; var length = 0;
            while (headerEnd < 0 || bytes.Count < headerEnd + 4 + length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token); if (read == 0) break;
                bytes.AddRange(buffer.AsSpan(0, read).ToArray());
                if (headerEnd < 0 && (headerEnd = FindHeaderEnd(bytes)) >= 0)
                {
                    var headerText = Encoding.ASCII.GetString(bytes.Take(headerEnd).ToArray());
                    var contentLength = Regex.Match(headerText, @"(?im)^Content-Length\s*:\s*(\d+)");
                    if (contentLength.Success) int.TryParse(contentLength.Groups[1].Value, out length);
                }
            }
            var separator = Math.Max(headerEnd, 0); var header = Encoding.ASCII.GetString(bytes.Take(separator).ToArray());
            var body = Encoding.UTF8.GetString(bytes.Skip(separator + 4).Take(length).ToArray());
            var lines = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries); var first = lines[0].Split(' ', 3);
            var headers = lines.Skip(1).Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2).ToDictionary(parts => parts[0], parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);
            return new(first[0], first[1], body, headers);
        }
        private static int FindHeaderEnd(List<byte> bytes)
        {
            for (var i = 3; i < bytes.Count; i++) if (bytes[i - 3] == 13 && bytes[i - 2] == 10 && bytes[i - 1] == 13 && bytes[i] == 10) return i - 3;
            return -1;
        }
    }

    private sealed record WireRequest(string Method, string Target, string Body, IReadOnlyDictionary<string, string> Headers);
    private sealed record WireResponse(int Status, string ContentType, string Body);
}
