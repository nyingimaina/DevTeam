using System.Text.Json;
using DevTeam.Broker.Rpc;

namespace DevTeam.Broker.Spoke;

/// <summary>
/// Drives <c>opencode acp</c> over stdio JSON-RPC via <see cref="JsonRpcConnection"/>.
/// Translates ACP frames into the typed <see cref="AgentEvent"/> stream every caller sees.
/// </summary>
public sealed class OpencodeAcpSpoke : IAgentSpoke
{
    private readonly JsonRpcConnection _connection;
    private readonly IPermissionPolicy _permissionPolicy;

    public OpencodeAcpSpoke(IAcpProcess process, IPermissionPolicy? permissionPolicy = null)
    {
        _permissionPolicy = permissionPolicy ?? new RejectAllPermissionPolicy();
        _connection = new JsonRpcConnection(process);
        _connection.ServerRequestHandler = HandleServerRequestAsync;
        _connection.NotificationReceived += OnNotificationReceived;
        _connection.Start();
    }

    public event EventHandler<AgentEvent>? EventReceived;

    public async Task<AgentInfo> InitializeAsync(CancellationToken cancellationToken)
    {
        var result = await _connection.SendAsync(
            "initialize",
            new { protocolVersion = 1, clientCapabilities = new { } },
            cancellationToken);

        var agentInfo = Lookup(result, "agentInfo");
        return new AgentInfo(
            GetString(agentInfo, "name") ?? "OpenCode",
            GetString(agentInfo, "version") ?? "?");
    }

    public async Task<AgentSession> NewSessionAsync(string cwd, CancellationToken cancellationToken)
    {
        var result = await _connection.SendAsync(
            "session/new",
            new { cwd, mcpServers = Array.Empty<object>() },
            cancellationToken);

        var sessionId = GetString(result, "sessionId") ?? string.Empty;
        return new AgentSession(sessionId, ParseConfigOptions(Lookup(result, "configOptions")));
    }

    public async Task<AgentPromptResult> PromptAsync(
        string sessionId,
        IReadOnlyList<AgentPromptPart> prompt,
        CancellationToken cancellationToken)
    {
        var payload = prompt.Select(p => new { type = p.Type, text = p.Text }).ToArray();
        var result = await _connection.SendAsync(
            "session/prompt",
            new { sessionId, prompt = payload },
            cancellationToken);

        return new AgentPromptResult(
            GetString(result, "stopReason") ?? "end_turn",
            ParseUsageInfo(Lookup(result, "usage")),
            GetString(result, "userMessageId"));
    }

    public Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken)
        => _connection.SendAsync("session/set_model", new { sessionId, modelId }, cancellationToken);

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
        => _connection.SendNotificationAsync("session/cancel", new { sessionId }, cancellationToken);

    public void Dispose() => _connection.Dispose();

    // ─── inbound frames ────────────────────────────────────────────────────────

    private Task<object?> HandleServerRequestAsync(JsonRpcServerRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "session/request_permission")
        {
            var sessionId = GetString(request.Params, "sessionId") ?? string.Empty;
            var toolCall = Lookup(request.Params, "toolCall");
            var requestInfo = new PermissionRequest(
                sessionId,
                GetString(toolCall, "toolCallId") ?? string.Empty,
                GetString(toolCall, "title"),
                OptionalElement(toolCall, "rawInput"));

            return DecidePermissionAsync(requestInfo, cancellationToken);
        }

        throw new RpcException("Method not found", -32601, JsonSerializer.Deserialize<JsonElement>("{\"method\":\"" + request.Method + "\"}"));
    }

    private async Task<object?> DecidePermissionAsync(PermissionRequest requestInfo, CancellationToken cancellationToken)
    {
        var verdict = await _permissionPolicy.DecideAsync(requestInfo, cancellationToken);
        return verdict switch
        {
            PermissionVerdict.AllowOnce => new
            {
                outcome = new { outcome = "selected", optionId = "once" },
            },
            PermissionVerdict.AllowAlways => new
            {
                outcome = new { outcome = "selected", optionId = "always" },
            },
            _ => new
            {
                outcome = new { outcome = "cancelled" },
            },
        };
    }

    private void OnNotificationReceived(object? sender, JsonRpcNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.Ordinal))
            return;

        var sessionId = GetString(notification.Params, "sessionId") ?? string.Empty;
        var update = Lookup(notification.Params, "update");
        var kind = GetString(update, "sessionUpdate") ?? string.Empty;

        AgentEvent? agentEvent = kind switch
        {
            "agent_message_chunk" => ParseTextChunk(sessionId, update, isThought: false),
            "agent_thought_chunk" => ParseTextChunk(sessionId, update, isThought: true),
            "tool_call" => new AgentToolCallEvent(sessionId, ParseToolCall(update)),
            "tool_call_update" => new AgentToolCallUpdatedEvent(sessionId, ParseToolCall(update)),
            "usage_update" => ParseUsageUpdate(sessionId, update),
            "config_option_update" => new AgentConfigOptionsUpdatedEvent(sessionId, ParseConfigOptions(Lookup(update, "configOptions"))),
            _ => new AgentUnknownEvent(sessionId, kind, update.ValueKind == JsonValueKind.Undefined ? default : update),
        };

        if (agentEvent is not null)
            EventReceived?.Invoke(this, agentEvent);
    }

    private static AgentEvent? ParseTextChunk(string sessionId, JsonElement update, bool isThought)
    {
        var messageId = GetString(update, "messageId");
        var content = Lookup(update, "content");
        var text = GetString(content, "text");
        if (messageId is null || text is null)
            return null;

        return isThought
            ? new AgentThoughtDelta(sessionId, messageId, text)
            : new AgentTextDelta(sessionId, messageId, text);
    }

    private static AgentToolCallInfo ParseToolCall(JsonElement update) => new(
        GetString(update, "toolCallId") ?? string.Empty,
        GetString(update, "title"),
        GetString(update, "kind"),
        GetString(update, "status"),
        OptionalElement(update, "rawInput"),
        OptionalElement(update, "rawOutput"));

    private static AgentUsageUpdatedEvent? ParseUsageUpdate(string sessionId, JsonElement update)
    {
        if (!update.TryGetProperty("used", out var usedElement) || usedElement.ValueKind != JsonValueKind.Number)
            return null;

        var cost = Lookup(update, "cost");
        return new AgentUsageUpdatedEvent(
            sessionId,
            new AgentTurnUsage(
                usedElement.GetInt64(),
                GetInt64(update, "size") ?? 0,
                GetDecimal(cost, "amount"),
                GetString(cost, "currency")));
    }

    private static AgentUsageInfo? ParseUsageInfo(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        return new AgentUsageInfo(
            GetInt64(usage, "inputTokens") ?? 0,
            GetInt64(usage, "outputTokens") ?? 0,
            GetInt64(usage, "totalTokens") ?? 0,
            GetInt64(usage, "cachedReadTokens"));
    }

    private static IReadOnlyList<AgentConfigOption> ParseConfigOptions(JsonElement configOptions)
    {
        var options = new List<AgentConfigOption>();
        if (configOptions.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var element in configOptions.EnumerateArray())
        {
            var values = new List<AgentConfigOptionValue>();
            if (Lookup(element, "options").ValueKind == JsonValueKind.Array)
            {
                foreach (var value in Lookup(element, "options").EnumerateArray())
                {
                    values.Add(new AgentConfigOptionValue(
                        GetString(value, "value") ?? string.Empty,
                        GetString(value, "name") ?? string.Empty,
                        GetString(value, "description")));
                }
            }

            options.Add(new AgentConfigOption(
                GetString(element, "id") ?? string.Empty,
                GetString(element, "name") ?? string.Empty,
                GetString(element, "category"),
                GetString(element, "type") ?? string.Empty,
                GetString(element, "currentValue"),
                values));
        }

        return options;
    }

    // ─── JsonElement helpers ───────────────────────────────────────────────────

    private static JsonElement Lookup(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) ? value : default;

    private static JsonElement? OptionalElement(JsonElement parent, string property)
        => parent.TryGetProperty(property, out var value) ? value : null;

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        return GetStringValue(Lookup(element, property));
    }

    private static string? GetStringValue(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static long? GetInt64(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var value = Lookup(element, property);
        return value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
    }

    private static decimal? GetDecimal(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        var value = Lookup(element, property);
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : null;
    }
}