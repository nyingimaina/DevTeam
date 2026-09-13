using System.Text.Json;

namespace DevTeam.Broker.Server;

public sealed record AgentViewModel(
    string ProtocolVersion,
    string AgentName,
    string AgentVendor,
    string AgentVersion,
    IReadOnlyList<ModelOption> Models);

public sealed record ModelOption(
    string Value,
    string Name,
    string? Description);

public sealed record SessionSummary(
    Guid SessionId,
    string AcpSessionId,
    string WorkspacePath,
    string? Title,
    string? ModelId,
    string? ModeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ModelOption> Models,
    IReadOnlyList<ModelOption> Modes);

public sealed record PartDto(
    Guid Id,
    string Kind,
    string? Text,
    string? ToolName,
    string? ToolCallId,
    string? ErrorText,
    JsonElement? InputJson,
    JsonElement? OutputJson,
    DateTimeOffset CreatedAt);

public sealed record MessageDto(
    Guid Id,
    string Role,
    string? AcpMessageId,
    string? ProviderModelId,
    string? BodyText,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PartDto> Parts);

public sealed record SessionDetail(
    Guid SessionId,
    string AcpSessionId,
    string WorkspacePath,
    string? Title,
    string? ModelId,
    string? ModeId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ModelOption> Models,
    IReadOnlyList<ModelOption> Modes,
    IReadOnlyList<MessageDto> Messages);

public sealed record PromptRequest(string Text, string? ModelId = null);

public sealed record PromptResponse(
    Guid SessionId,
    string StopReason,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

public sealed record SetModelRequest(string ModelId);

public sealed record SetModeRequest(string ModeId);

public sealed record NewSessionRequest(string WorkspacePath, string? ModelId = null);

public sealed record HealthResponse(string Status, string? Version = null);

public sealed record UsageDto(long InputTokens, long OutputTokens, long TotalTokens, long? CachedReadTokens);

public sealed record ConfigOptionDto(string ConfigId, string CurrentValue, IReadOnlyList<ModelOption> Options);

/// <summary>One hub-broadcast frame. Type is a stable discriminator the UI switches on.</summary>
public sealed record StreamEvent(string SessionId, string Type, JsonElement? Payload,
    string? SessionTitle = null, DateTimeOffset? At = null);

// ─── filesystem browser (PathBrowser) ────────────────────────────────────────

public sealed record FileSystemRootDto(string Path, string DisplayName);

public sealed record FileSystemEntryDto(string Name, string FullPath, string Kind, long? SizeBytes);

public sealed record FileSystemStatDto(string Name, string Kind, bool Exists, bool IsGitRepository);

public sealed record CreateDirectoryRequest(string Path);