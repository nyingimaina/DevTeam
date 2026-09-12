namespace DevTeam.Broker.Spoke;

/// <summary>
/// The broker's narrow, transport-agnostic view of one agent backend (ACP now,
/// an HTTP/remote agent later). A connection owns exactly one process and one fix.
/// </summary>
public interface IAgentSpoke : IDisposable
{
    /// <summary>Raised for every streamed ACP update: text deltas, thoughts, tool calls, usage.</summary>
    event EventHandler<AgentEvent>? EventReceived;

    Task<AgentInfo> InitializeAsync(CancellationToken cancellationToken);

    Task<AgentSession> NewSessionAsync(string cwd, CancellationToken cancellationToken);

    Task<AgentPromptResult> PromptAsync(
        string sessionId,
        IReadOnlyList<AgentPromptPart> prompt,
        CancellationToken cancellationToken);

    Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken);

    Task CancelAsync(string sessionId, CancellationToken cancellationToken);
}