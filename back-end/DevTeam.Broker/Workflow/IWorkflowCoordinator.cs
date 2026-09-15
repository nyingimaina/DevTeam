using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;

namespace DevTeam.Broker.Workflow;

public interface IWorkflowCoordinator
{
    Task<SessionSummary> NewSessionAsync(string workspacePath, string? modelId, IReadOnlyList<string>? allowedWritePrefixes, CancellationToken ct);
    Task<string> SetModeAsync(Guid sessionId, string modeId, CancellationToken ct);
    Task<PromptResponse> PromptWithSessionRecoveryAsync(Guid sessionId, string text, CancellationToken ct, bool isPriming = false);
}
