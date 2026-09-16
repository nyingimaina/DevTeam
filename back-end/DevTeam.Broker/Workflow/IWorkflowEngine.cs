using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;

namespace DevTeam.Broker.Workflow;

public interface IWorkflowEngine
{
    Task<DevTeamRelease> StartReleaseAsync(string featureKey, string workspacePath, CancellationToken ct);
    Task<ReleaseFeature> CreateFeatureAsync(Guid releaseId, string featureKey, CancellationToken ct);
    Task<DevTeamRelease> SwitchFeatureAsync(Guid featureId, CancellationToken ct);
    Task<DevTeamRelease> AdvanceAsync(Guid featureId, CancellationToken ct);
    Task<ReleaseStageRun> StartStageAsync(Guid featureId, CancellationToken ct);
    Task<StagePromptResult> SendMessageAsync(Guid featureId, string text, CancellationToken ct);
    Task<StagePromptResult> SendMessageEnforcingSingleQuestionAsync(Guid featureId, string text, CancellationToken ct);
    Task<DevTeamRelease> RunGatesAsync(Guid featureId, CancellationToken ct);
    Task<DevTeamRelease> RunStageAsync(Guid featureId, CancellationToken ct);
    Task<DevTeamRelease> PushBackAsync(Guid featureId, string targetStageName, string? instructions, CancellationToken ct);
    Task<IReadOnlyList<MessageDto>> GetStageMessagesAsync(Guid featureId, Guid stageRunId, CancellationToken ct);
    Task<IReadOnlyList<PipelineStageDto>> GetPipelineAsync(Guid featureId, CancellationToken ct);
    Task<IReadOnlyList<StageArtifactDto>> GetStageArtifactsAsync(Guid featureId, Guid stageRunId, CancellationToken ct);
    Task<IReadOnlyList<string>> GetWorkspaceChangesAsync(Guid featureId, CancellationToken ct);
    Task<DevTeamRelease> SignoffAsync(Guid featureId, string stageName, string role, string? comment, CancellationToken ct);
    Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct);
    Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(string? workspacePath, CancellationToken ct);
    Task<IReadOnlyList<ModelOption>> GetAvailableModelsAsync(Guid releaseId, CancellationToken ct);
}

public sealed record StagePromptResult(
    string Response,
    long InputTokens,
    long OutputTokens,
    long TotalTokens);

public sealed record StepExecutionResult
{
    public string StepName { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string? Reason { get; init; }
    public string? Evidence { get; init; }
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];
    // The role that should act when this specific gate fails — see WorkflowStep.ResponsibleRole.
    // Null means "the role currently running owns this," i.e. today's implicit default.
    public string? ResponsibleRole { get; init; }
}
