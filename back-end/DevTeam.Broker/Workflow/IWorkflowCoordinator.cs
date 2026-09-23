using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;

namespace DevTeam.Broker.Workflow;

public interface IWorkflowCoordinator
{
    Task<SessionSummary> NewSessionAsync(string workspacePath, string? modelId, IReadOnlyList<string>? allowedWritePrefixes, CancellationToken ct);
    Task<string> SetModeAsync(Guid sessionId, string modeId, CancellationToken ct);
    /// <summary>
    /// Sends <paramref name="text"/> to the agent. <paramref name="displayText"/>, when given, is
    /// what gets stored and shown as the user's message — so DevTeam can append its own standing
    /// instructions for the model without putting them in the user's chat bubble.
    /// </summary>
    Task<PromptResponse> PromptWithSessionRecoveryAsync(
        Guid sessionId, string text, CancellationToken ct, bool isPriming = false, string? displayText = null);
}
