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
    IReadOnlyList<PartDto> Parts,
    bool IsPriming);

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

public sealed record PromptResponse(
    Guid SessionId,
    string StopReason,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

public sealed record SetModelRequest(string ModelId);

public sealed record SetModeRequest(string ModeId);

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

public sealed record RevealInExplorerRequest(string Path);

public sealed record CleanupWorkspaceRequest(string WorkspacePath);

// ─── release endpoints ─────────────────────────────────────────────────────

public sealed record CreateReleaseRequest(string FeatureKey, string WorkspacePath);

public sealed record CreateFeatureRequest(string FeatureKey);

public sealed record SignoffRequest(string StageName, string Role, string? Comment);

// ─── stage endpoints ───────────────────────────────────────────────────────

public sealed record SendMessageRequest(string Text);

public sealed record PushBackRequest(string TargetStageName, string? Instructions);

public sealed record PipelineStageDto(string Name, bool UserInputRequired, string? Signoff, IReadOnlyList<string> ExpectedArtifacts, IReadOnlyList<string> Steps);

public sealed record StageArtifactDto(string RelativePath, string? Content);

// ─── git endpoints ─────────────────────────────────────────────────────────

public sealed record GitInitRequest(string WorkspacePath);

public sealed record GitBranchRequest(string WorkspacePath, string BranchName);

public sealed record GitCommitRequest(string WorkspacePath, string Message);

public sealed record GitMergeRequest(string WorkspacePath, string SourceBranch, string? TargetBranch = null);

public sealed record GitRemoteResponse(string? Url, string? CredentialName = null);

public sealed record SetGitRemoteRequest(string WorkspacePath, string Url, string? CredentialName = null);

public sealed record SetGitCredentialRequest(string Name, string Token);

public sealed record GitCredentialsResponse(IReadOnlyList<string> Names);

// ─── profiles ───────────────────────────────────────────────────────────────

public sealed record ProfilePromptDto(string StageName, string PromptText, bool OverridesBuiltInPrompt);

public sealed record ProfileDto(Guid Id, string Name, string? Description, bool IsDefault, IReadOnlyList<ProfilePromptDto> Prompts);

public sealed record CreateProfileRequest(string Name, string? Description);

public sealed record UpdateProfileRequest(string Name, string? Description, IReadOnlyList<ProfilePromptDto> Prompts);

public sealed record WorkspaceProfileDto(Guid? ProfileId);

public sealed record SetWorkspaceProfileRequest(string WorkspacePath, Guid ProfileId);

// ─── specialists ──────────────────────────────────────────────────────────────

public sealed record SpecialistRoleDto(Guid Id, string Name, string Description, string PrimingPrompt, bool WritesCode);

public sealed record SaveSpecialistRoleRequest(string Name, string Description, string PrimingPrompt, bool WritesCode);

public sealed record SpecialistConsultationDto(Guid Id, Guid StageRunId, string SpecialistName, string Question, string ResponseText, DateTimeOffset CreatedAt);