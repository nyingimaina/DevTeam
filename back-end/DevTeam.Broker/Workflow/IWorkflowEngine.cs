using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Workflow;

public interface IWorkflowEngine
{
    Task<DevTeamRelease> StartReleaseAsync(string featureKey, string workspacePath, CancellationToken ct);
    Task<DevTeamRelease> AdvanceAsync(Guid releaseId, CancellationToken ct);
    Task<DevTeamRelease> SignoffAsync(Guid releaseId, string stageName, string role, string? comment, CancellationToken ct);
    Task<DevTeamRelease> GetReleaseAsync(Guid releaseId, CancellationToken ct);
    Task<IReadOnlyList<DevTeamRelease>> ListReleasesAsync(CancellationToken ct);
}

public sealed record StepExecutionResult
{
    public string StepName { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string? Evidence { get; init; }
    public IReadOnlyList<ReviewFinding> Findings { get; init; } = [];
}
