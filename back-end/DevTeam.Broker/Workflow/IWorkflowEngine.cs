using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;

namespace DevTeam.Broker.Workflow;

public interface IWorkflowEngine
{
    Task<DevTeamRelease> StartReleaseAsync(string featureKey, string workspacePath, CancellationToken ct);
    Task<DevTeamRelease> AdvanceAsync(Guid releaseId, CancellationToken ct);
    Task<ReleaseStageRun> StartStageAsync(Guid releaseId, CancellationToken ct);
    Task<StagePromptResult> SendMessageAsync(Guid releaseId, string text, CancellationToken ct);
    Task<StagePromptResult> SendMessageEnforcingSingleQuestionAsync(Guid releaseId, string text, CancellationToken ct);
    Task<DevTeamRelease> RunGatesAsync(Guid releaseId, CancellationToken ct);
    Task<DevTeamRelease> RunStageAsync(Guid releaseId, CancellationToken ct);
    Task<DevTeamRelease> PushBackAsync(Guid releaseId, string targetStageName, string? instructions, CancellationToken ct);
    Task<IReadOnlyList<MessageDto>> GetStageMessagesAsync(Guid releaseId, Guid stageRunId, CancellationToken ct);
    Task<IReadOnlyList<PipelineStageDto>> GetPipelineAsync(Guid releaseId, CancellationToken ct);
    Task<DevTeamRelease> SignoffAsync(Guid releaseId, string stageName, string role, string? comment, CancellationToken ct);
    Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct);
    Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(CancellationToken ct);
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
    public string? Evidence { get; init; }
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];
}
