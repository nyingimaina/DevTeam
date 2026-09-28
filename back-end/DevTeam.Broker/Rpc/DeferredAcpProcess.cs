namespace DevTeam.Broker.Rpc;

/// <summary>
/// Defers launching the agent process until the ACP channel is actually used.
/// </summary>
/// <remarks>
/// <see cref="IAcpProcess"/> is a singleton whose factory starts <c>opencode acp</c>, so anything
/// that merely resolved it started an agent — including the workspace process sweep, which only
/// wants the pid to exclude from its kill list. Launching here instead means the process exists
/// only once someone talks to it, and a failed launch surfaces on that call rather than on
/// whatever request happened to resolve the service.
/// </remarks>
public sealed class DeferredAcpProcess : IAcpProcess
{
    private readonly Func<IAcpProcess> _factory;
    private readonly object _gate = new();
    private IAcpProcess? _inner;

    public DeferredAcpProcess(Func<IAcpProcess> factory) => _factory = factory;

    /// <summary>
    /// The running agent's pid, or 0 when no agent has been launched. Deliberately does not launch
    /// anything: the process sweep uses this to build an exclusion list, and an exclusion list is
    /// not a reason to start a process.
    /// </summary>
    public int ProcessId => Volatile.Read(ref _inner)?.ProcessId ?? 0;

    private IAcpProcess Inner
    {
        get
        {
            var existing = Volatile.Read(ref _inner);
            if (existing is not null)
                return existing;

            lock (_gate)
            {
                if (_inner is not null)
                    return _inner;

                var created = _factory();
                Volatile.Write(ref _inner, created);
                return created;
            }
        }
    }

    public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        => Inner.ReadLineAsync(cancellationToken);

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        => Inner.WriteLineAsync(line, cancellationToken);

    public void Kill() => Volatile.Read(ref _inner)?.Kill();

    public void Dispose()
    {
        IAcpProcess? inner;
        lock (_gate)
        {
            inner = _inner;
            _inner = null;
        }

        inner?.Dispose();
    }
}
