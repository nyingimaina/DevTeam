using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace DevTeam.Broker.Rpc;

/// <summary>A client role of the ACP protocol (newline-delimited JSON-RPC over stdio).</summary>
public sealed class JsonRpcConnection : IDisposable
{
    public const int InternalErrorCode = -32603;
    public const int MethodNotFoundCode = -32601;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IAcpProcess _process;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _nextId = 1;
    private int _started;
    private int _disposed;

    /// <summary>Invoked for server-&gt;client requests (e.g. <c>session/request_permission</c>).</summary>
    public Func<JsonRpcServerRequest, CancellationToken, Task<object?>>? ServerRequestHandler { get; set; }

    /// <summary>Raised for server-&gt;client notifications (e.g. <c>session/update</c>).</summary>
    public event EventHandler<JsonRpcNotification>? NotificationReceived;

    public JsonRpcConnection(IAcpProcess process, TimeSpan? requestTimeout = null)
    {
        _process = process;
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>Spawns the read loop; idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>Sends a request and awaits the matching response.</summary>
    public async Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = _nextId++;
        var idKey = id.ToString(CultureInfo.InvariantCulture);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_requestTimeout);

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[idKey] = completion;

        try
        {
            var frame = new
            {
                jsonrpc = "2.0",
                method,
                id,
                @params = parameters ?? new { },
            };
            await WriteFrameAsync(JsonSerializer.Serialize(frame, SerializerOptions), timeout.Token);

            return await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _pending.TryRemove(idKey, out _);
                throw new AcpTimeoutException(method, _requestTimeout);
            }

            _pending.TryRemove(idKey, out _);
            throw;
        }
        finally
        {
            _pending.TryRemove(idKey, out _);
        }
    }

    /// <summary>Sends a server-&gt;client notification (no response expected).</summary>
    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var frame = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters ?? new { },
        };
        await WriteFrameAsync(JsonSerializer.Serialize(frame, SerializerOptions), cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        _process.Dispose();
        FailPending(new AcpDisconnectedException("ACP connection disposed"));
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task WriteFrameAsync(string frame, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process.WriteLineAsync(frame, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Ingest loop: responses -&gt; pending completions, notifications -&gt; event, requests -&gt; handler.</summary>
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var line = await _process.ReadLineAsync(_lifetime.Token);
                if (line is null)
                    break;

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    await HandleFrameAsync(JsonSerializer.Deserialize<JsonElement>(line));
                }
                catch (JsonException)
                {
                    // Non-JSON line (e.g. stray stderr routed to stdout): ignore.
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // ONE bad frame must never kill the ingest loop. If it did, every pending
                    // request would hang until its request timeout and the caller would see a
                    // silent, unexplained stall with no events at all — exactly the failure this
                    // guards against.
                    continue;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown via Dispose.
        }
        finally
        {
            // ALWAYS fail whatever is still pending: an exited or broken connection must surface
            // as a failed request immediately, never as a request that waits for its timeout.
            FailPending(new AcpDisconnectedException("ACP connection closed"));
        }
    }

    private async Task HandleFrameAsync(JsonElement frame)
    {
        if (frame.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString() ?? string.Empty;
            var parameters = frame.TryGetProperty("params", out var p) ? p : default;

            if (frame.TryGetProperty("id", out var requestId))
            {
                await HandleServerRequestAsync(requestId, method, parameters);
                return;
            }

            NotificationReceived?.Invoke(this, new JsonRpcNotification(method, parameters));
            return;
        }

        if (!frame.TryGetProperty("id", out var responseId))
            return;

        var idRaw = ResponseIdKey(responseId);
        if (idRaw is null || !_pending.TryRemove(idRaw, out var completion))
            return;

        if (frame.TryGetProperty("error", out var errorElement))
        {
            completion.TrySetException(RpcExceptionFrom(errorElement));
            return;
        }

        if (frame.TryGetProperty("result", out var resultElement))
        {
            completion.TrySetResult(CloneDeep(resultElement));
            return;
        }

        completion.TrySetException(new RpcException("Response had neither result nor error", InternalErrorCode));
    }

    private async Task HandleServerRequestAsync(JsonElement idElement, string method, JsonElement parameters)
    {
        try
        {
            var handler = ServerRequestHandler;
            object? result;
            if (handler is null)
            {
                var notFound = new
                {
                    jsonrpc = "2.0",
                    id = CloneDeep(idElement),
                    error = new { code = MethodNotFoundCode, message = $"Method not found: {method}" },
                };
                await WriteFrameAsync(JsonSerializer.Serialize(notFound, SerializerOptions), _lifetime.Token);
                return;
            }

            result = await handler(
                new JsonRpcServerRequest(CloneDeep(idElement), method, CloneDeep(parameters)),
                _lifetime.Token);

            var response = new
            {
                jsonrpc = "2.0",
                id = CloneDeep(idElement),
                result = result ?? new { },
            };
            await WriteFrameAsync(JsonSerializer.Serialize(response, SerializerOptions), _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // Peer going away; nothing to do.
        }
        catch (RpcException ex)
        {
            var error = new
            {
                jsonrpc = "2.0",
                id = CloneDeep(idElement),
                error = new { code = ex.Code, message = ex.Message },
            };
            try
            {
                await WriteFrameAsync(JsonSerializer.Serialize(error, SerializerOptions), _lifetime.Token);
            }
            catch (Exception)
            {
                // Peer went away mid-handshake.
            }
        }
        catch (Exception ex)
        {
            var error = new
            {
                jsonrpc = "2.0",
                id = CloneDeep(idElement),
                error = new { code = InternalErrorCode, message = $"Handler failed: {ex.Message}" },
            };
            try
            {
                await WriteFrameAsync(JsonSerializer.Serialize(error, SerializerOptions), _lifetime.Token);
            }
            catch (Exception)
            {
                // Peer went away mid-handshake.
            }
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
            completion.TrySetException(exception);
        _pending.Clear();
    }

    private static string? ResponseIdKey(JsonElement id)
    {
        if (id.ValueKind == JsonValueKind.Number)
            return id.GetRawText();
        if (id.ValueKind == JsonValueKind.String)
            return id.GetString();
        return null;
    }

    private static JsonElement CloneDeep(JsonElement source)
        => JsonSerializer.Deserialize<JsonElement>(source.GetRawText());

    private static RpcException RpcExceptionFrom(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var codeValue)
            ? codeValue
            : InternalErrorCode;
        var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
            ? m.GetString()!
            : "ACP error";
        var data = error.TryGetProperty("data", out var d) ? d : (JsonElement?)null;
        return new RpcException(message, code, data);
    }
}

/// <summary>An incoming server-&gt;client JSON-RPC request (requires a response).</summary>
public sealed record JsonRpcServerRequest(JsonElement Id, string Method, JsonElement Params);

/// <summary>An incoming server-&gt;client JSON-RPC notification (fire and forget).</summary>
public sealed record JsonRpcNotification(string Method, JsonElement Params);
