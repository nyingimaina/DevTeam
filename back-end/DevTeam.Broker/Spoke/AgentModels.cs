using System.Text.Json;

namespace DevTeam.Broker.Spoke;

public sealed record AgentInfo(string Name, string Version);

public sealed record AgentSession(string SessionId, IReadOnlyList<AgentConfigOption> ConfigOptions);

public sealed record AgentPromptResult(string StopReason, AgentUsageInfo? Usage, string? UserMessageId);

public sealed record AgentUsageInfo(long InputTokens, long OutputTokens, long TotalTokens, long? CachedReadTokens);

public sealed record AgentConfigOption(
    string Id,
    string Name,
    string? Category,
    string Type,
    string? CurrentValue,
    IReadOnlyList<AgentConfigOptionValue> Options);

public sealed record AgentConfigOptionValue(string Value, string Name, string? Description);

public sealed record AgentPromptPart(string Type, string Text);

public sealed record AgentToolCallInfo(
    string ToolCallId,
    string? Title,
    string? Kind,
    string? Status,
    JsonElement? RawInput,
    JsonElement? RawOutput);

public sealed record AgentTurnUsage(long UsedTokens, long ContextSize, decimal? CostAmount, string? CostCurrency);

// ─── streamed events ──────────────────────────────────────────────────────────

public abstract record AgentEvent(string SessionId);

public sealed record AgentTextDelta(string SessionId, string MessageId, string Text) : AgentEvent(SessionId);

public sealed record AgentThoughtDelta(string SessionId, string MessageId, string Text) : AgentEvent(SessionId);

public sealed record AgentToolCallEvent(string SessionId, AgentToolCallInfo Call) : AgentEvent(SessionId);

public sealed record AgentToolCallUpdatedEvent(string SessionId, AgentToolCallInfo Call) : AgentEvent(SessionId);

public sealed record AgentUsageUpdatedEvent(string SessionId, AgentTurnUsage Usage) : AgentEvent(SessionId);

public sealed record AgentConfigOptionsUpdatedEvent(string SessionId, IReadOnlyList<AgentConfigOption> Options) : AgentEvent(SessionId);

public sealed record AgentUnknownEvent(string SessionId, string UpdateKind, JsonElement Update) : AgentEvent(SessionId);