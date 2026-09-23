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
    IReadOnlyList<ModelOption> Modes,
    // What was asked for; ModelId is what is actually in effect. They differ when the agent
    // refused (or doesn't know) the requested model — the UI shows that instead of lying.
    string? RequestedModelId = null);

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
    IReadOnlyList<MessageDto> Messages,
    // See SessionSummary.RequestedModelId.
    string? RequestedModelId = null);

/// <summary>
/// How a turn went, beyond its token counts: wall-clock, warm-up lag and event mix. The engine
/// persists these on a <c>TurnMetric</c>; the coordinator is the only thing that can measure them.
/// </summary>
public sealed record TurnMeasurement(
    long DurationMs,
    long? TimeToFirstEventMs,
    int TextEvents,
    int ThoughtEvents,
    int ToolEvents,
    string Outcome = "Ok",
    long? CachedReadTokens = null,
    long? ContextTokens = null,
    decimal? CostAmount = null,
    string? CostCurrency = null);

public sealed record PromptResponse(
    Guid SessionId,
    string StopReason,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    TurnMeasurement? Measurement = null);

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

public sealed record SetAutonomousEnabledRequest(bool Enabled);

public sealed record CreateHotfixRequest(string Key, string WorkspacePath);

public sealed record SignoffRequest(string StageName, string Role, string? Comment);

// ─── stage endpoints ───────────────────────────────────────────────────────

public sealed record SendMessageRequest(string Text);

public sealed record PushBackRequest(string TargetStageName, string? Instructions);

public sealed record RetryStageRequest(string? TargetStageName);

// StepLabels runs parallel to Steps: the same identifiers, each with the plain-language wording
// the UI shows instead (see StepFriendlyText). Keeping both lets diagnostics/tests still address
// a step by its stable id while the checklist reads like English.
public sealed record PipelineStageDto(
    string Name,
    bool UserInputRequired,
    string? Signoff,
    IReadOnlyList<string> ExpectedArtifacts,
    IReadOnlyList<string> Steps,
    IReadOnlyList<string> StepLabels);

public sealed record StageArtifactDto(string RelativePath, string? Content);

// ─── git endpoints ─────────────────────────────────────────────────────────

public sealed record GitInitRequest(string WorkspacePath);

public sealed record GitBranchRequest(string WorkspacePath, string BranchName);

public sealed record GitCommitRequest(string WorkspacePath, string Message);

public sealed record GitMergeRequest(string WorkspacePath, string SourceBranch, string? TargetBranch = null);

// ─── diagnostics (support hand-off) ────────────────────────────────────────

public sealed record DiagnosticsSettingsDto(bool VerboseLogging, string LogsDirectory);

public sealed record DiagnosticsSettingsRequest(bool VerboseLogging);

// ─── model candidates (the failover list) ──────────────────────────────────

/// <summary>
/// One entry in a workspace's model list. Cost/Smartness/Note are read-only estimates from the
/// curated catalogue (null for a model we have no estimate for — shown as "unknown").
/// </summary>
public sealed record ModelCandidateDto(
    Guid Id,
    string ModelId,
    int Priority,
    bool Enabled,
    bool UserAdded,
    DateTimeOffset? CooldownUntil,
    string? LastFailureKind,
    string? LastFailureReason,
    int? Cost,
    int? Smartness,
    string? Note);

public sealed record AddModelCandidateRequest(string WorkspacePath, string ModelId);

public sealed record ReorderModelCandidatesRequest(string WorkspacePath, Guid[] OrderedIds);

public sealed record SetModelCandidateEnabledRequest(bool Enabled);

// ─── desktop notifications ─────────────────────────────────────────────────

public sealed record NotificationSettingsDto(bool StageComplete, bool NeedsAttention, bool ApprovalNeeded, bool Sound);

public sealed record NotificationSettingsRequest(bool? StageComplete, bool? NeedsAttention, bool? ApprovalNeeded, bool? Sound);

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

// ── code overview (the short project map agents read first) ────────────────

public sealed record CodeContextStatusDto(string State, int? ChangesBehind, DateTimeOffset? BuiltAt, IReadOnlyList<string> Warnings);

// ── requirement progress (deterministic code/tests completion) ─────────────

public sealed record ProgressCountDto(int Done, int Total);

public sealed record RequirementProgressDto(int Requirements, ProgressCountDto Code, ProgressCountDto Tests);

// ── negotiation (the numbered points stages exchange on a push-back) ───────

public sealed record NegotiationPointDto(
    Guid Id,
    string Target,
    string Summary,
    string? Expected,
    int Round,
    string? OpenedBy,
    string? PushedBackTo,
    string Status,
    string ResponseKind,
    string? ResponseText,
    string? RequirementRef,
    DateTimeOffset CreatedAt);