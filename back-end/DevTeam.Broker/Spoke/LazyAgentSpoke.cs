using DevTeam.Broker.Rpc;

namespace DevTeam.Broker.Spoke;

/// <summary>
/// Defers creating the real agent spoke — and therefore launching <c>opencode acp</c> — until an
/// agent call is actually made.
/// </summary>
/// <remarks>
/// The spoke sits on the dependency chain of the workflow engine, which every release and feature
/// endpoint resolves. Constructing <see cref="OpencodeAcpSpoke"/> starts the ACP connection in its
/// constructor, so an eager registration made a read-only request such as
/// <c>GET /api/releases</c> depend on the agent being launchable: when the launch failed, the
/// factory threw and the unrelated endpoint answered 500. Deferring the construction keeps the
/// launch failure where it belongs — on the agent call that needs it, where it can be reported to
/// the user as a plain-language error.
/// </remarks>
public sealed class LazyAgentSpoke : IAgentSpoke
{
    private readonly Func<IAgentSpoke> _factory;
    private readonly object _gate = new();
    private IAgentSpoke? _inner;
    private bool _disposed;

    public LazyAgentSpoke(Func<IAgentSpoke> factory) => _factory = factory;

    public event EventHandler<AgentEvent>? EventReceived
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            // Attach to the real spoke as soon as it exists, so no event is missed between
            // construction and the first agent call.
            if (Volatile.Read(ref _inner) is { } existing)
                existing.EventReceived += value;
            else
                _pendingHandlers.Add(value);
        }
        remove
        {
            if (Volatile.Read(ref _inner) is { } existing)
                existing.EventReceived -= value;
            else
                _pendingHandlers.Remove(value);
        }
    }

    private readonly List<EventHandler<AgentEvent>> _pendingHandlers = [];

    private IAgentSpoke Inner
    {
        get
        {
            var existing = Volatile.Read(ref _inner);
            if (existing is not null)
                return existing;

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_inner is not null)
                    return _inner;

                var created = _factory();
                foreach (var handler in _pendingHandlers)
                    created.EventReceived += handler;
                _pendingHandlers.Clear();
                Volatile.Write(ref _inner, created);
                return created;
            }
        }
    }

    public Task<AgentInfo> InitializeAsync(CancellationToken cancellationToken)
        => Inner.InitializeAsync(cancellationToken);

    public Task<AgentSession> NewSessionAsync(string cwd, CancellationToken cancellationToken)
        => Inner.NewSessionAsync(cwd, cancellationToken);

    public Task<AgentPromptResult> PromptAsync(
        string sessionId,
        IReadOnlyList<AgentPromptPart> prompt,
        CancellationToken cancellationToken) =>
        Inner.PromptAsync(sessionId, prompt, cancellationToken);

    public Task SetModelAsync(string sessionId, string modelId, CancellationToken cancellationToken)
        => Inner.SetModelAsync(sessionId, modelId, cancellationToken);

    public Task SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken)
        => Inner.SetModeAsync(sessionId, modeId, cancellationToken);

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
        => Inner.CancelAsync(sessionId, cancellationToken);

    public void Dispose()
    {
        IAgentSpoke? inner;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            inner = _inner;
        }

        // Nothing was ever created, so there is nothing to tear down.
        inner?.Dispose();
    }
}
