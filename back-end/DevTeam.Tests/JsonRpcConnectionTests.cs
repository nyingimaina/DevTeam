using System.Text.Json;
using DevTeam.Broker.Rpc;
using DevTeam.Tests.Rpc;

namespace DevTeam.Tests;

public class JsonRpcConnectionTests : IDisposable
{
    private readonly AcpPipeHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task Send_SerializeRequest_AndMatchResponseById()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.Start();

        var pending = connection.SendAsync("initialize", new { protocolVersion = 1, clientCapabilities = new { } }, CancellationToken.None);

        var (method, idRaw, paramsJson) = await _harness.ReadRequestAsync();
        Assert.Equal("initialize", method);
        using var paramsDoc = JsonDocument.Parse(paramsJson);
        Assert.Equal(1, paramsDoc.RootElement.GetProperty("protocolVersion").GetInt32());

        _harness.Emit($"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"result\":{{\"ok\":true,\"agentInfo\":{{\"name\":\"OpenCode\",\"version\":\"1.18.14\"}}}}}}");

        var result = await pending;
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Send_ErrorResponse_ThrowsRpcException()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.Start();

        var pending = connection.SendAsync("session/new", new { cwd = "C:\\x" }, CancellationToken.None);

        var (_, idRaw, _) = await _harness.ReadRequestAsync();
        _harness.Emit($"{{\"jsonrpc\":\"2.0\",\"id\":{idRaw},\"error\":{{\"code\":-32000,\"message\":\"boom\"}}}}");

        var ex = await Assert.ThrowsAsync<RpcException>(() => pending);
        Assert.Equal(-32000, ex.Code);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task Send_Timeout_ThrowsTimeoutException()
    {
        var connection = new JsonRpcConnection(_harness.Process, requestTimeout: TimeSpan.FromMilliseconds(150));
        connection.Start();

        var pending = connection.SendAsync("session/prompt", new { sessionId = "s1" }, CancellationToken.None);

        await _harness.ReadRequestAsync();
        await Assert.ThrowsAsync<AcpTimeoutException>(() => pending);
    }

    [Fact]
    public async Task Send_CallerCancellation_SurfacesOperationCanceledNotTimeout()
    {
        var connection = new JsonRpcConnection(_harness.Process, requestTimeout: TimeSpan.FromMinutes(5));
        connection.Start();

        using var callerCts = new CancellationTokenSource();
        var pending = connection.SendAsync("session/prompt", new { sessionId = "s1" }, callerCts.Token);

        await _harness.ReadRequestAsync();
        callerCts.Cancel();

        // A caller-initiated cancel must surface as cancellation, not as an internal AcpTimeout.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Notification_WithoutId_RaisesEvent()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.Start();

        var tcs = new TaskCompletionSource<JsonRpcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (_, n) => tcs.TrySetResult(n);

        _harness.EmitSessionUpdate("s1", "{\"sessionUpdate\":\"usage_update\",\"used\":123}");

        var notification = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("session/update", notification.Method);
        Assert.Equal("s1", notification.Params.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task ServerRequest_HandlerReply_IsSentBack()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.ServerRequestHandler = (request, _) => Task.FromResult<object?>(
            new { outcome = new { outcome = "cancelled" } });
        connection.Start();

        _harness.Emit("{"
                      + "\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"session/request_permission\","
                      + "\"params\":{\"sessionId\":\"s1\",\"toolCall\":{\"toolCallId\":\"c1\"},\"options\":[]}}");

        var frame = await _harness.ReadClientFrameAsync();
        using var doc = JsonDocument.Parse(frame);
        var root = doc.RootElement;
        Assert.Equal(7, root.GetProperty("id").GetInt32());
        Assert.Equal("cancelled", root.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task ServerRequest_UnknownMethod_HasNoHandler_ReturnsMethodNotFoundError()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.Start();

        _harness.Emit("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"unknown/method\",\"params\":{}}");

        var frame = await _harness.ReadClientFrameAsync();
        using var doc = JsonDocument.Parse(frame);
        Assert.Equal(9, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ProcessExit_FailsPendingRequests()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.Start();

        var pending = connection.SendAsync("session/prompt", new { sessionId = "s1" }, CancellationToken.None);
        await _harness.ReadRequestAsync();

        _harness.SimulateProcessExit();

        await Assert.ThrowsAnyAsync<IOException>(() => pending);
    }

    [Fact]
    public async Task ConcurrentSends_MatchResponsesByTheirOwnId()
    {
        var connection = new JsonRpcConnection(_harness.Process);
        connection.Start();

        var first = connection.SendAsync("initialize", new { }, CancellationToken.None);
        var second = connection.SendAsync("authenticate", new { methodId = "opencode-login" }, CancellationToken.None);

        var (method1, id1, _) = await _harness.ReadRequestAsync();
        var (method2, id2, _) = await _harness.ReadRequestAsync();
        Assert.NotEqual(id1, id2);

        _harness.Emit($"{{\"jsonrpc\":\"2.0\",\"id\":{id1},\"result\":{{\"who\":\"first\"}}}}");
        _harness.Emit($"{{\"jsonrpc\":\"2.0\",\"id\":{id2},\"result\":{{\"who\":\"second\"}}}}");

        Assert.Equal("first", (await first).GetProperty("who").GetString());
        Assert.Equal("second", (await second).GetProperty("who").GetString());
    }
}