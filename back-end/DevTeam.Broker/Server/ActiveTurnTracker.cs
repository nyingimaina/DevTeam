namespace DevTeam.Broker.Server;

/// <summary>What's currently occupying the broker's single agent-turn slot.</summary>
public sealed record ActiveTurnInfo(Guid SessionId, string AcpSessionId, string Preview, DateTimeOffset StartedAt);

/// <summary>
/// Tracks whichever agent turn currently holds <c>BrokerCoordinator</c>'s turn lock, so
/// the UI can show what's running (or stuck) and cancel it — the broker drives one
/// opencode process shared by every release, so only one turn is ever active at a time.
/// </summary>
public sealed class ActiveTurnTracker
{
    private readonly object _gate = new();
    private (ActiveTurnInfo Info, CancellationTokenSource Cts)? _current;

    public ActiveTurnInfo? Current
    {
        get { lock (_gate) return _current?.Info; }
    }

    /// <summary>Marks a turn as active until the returned scope is disposed.</summary>
    public IDisposable Begin(Guid sessionId, string acpSessionId, string text, CancellationTokenSource cts)
    {
        var preview = text.Length > 80 ? text[..80] : text;
        var info = new ActiveTurnInfo(sessionId, acpSessionId, preview, DateTimeOffset.UtcNow);
        lock (_gate) _current = (info, cts);
        return new Scope(this);
    }

    /// <summary>Cancels the active turn's token, if any. Returns what was cancelled.</summary>
    public bool TryCancelCurrent(out ActiveTurnInfo? info)
    {
        (ActiveTurnInfo Info, CancellationTokenSource Cts)? current;
        lock (_gate) current = _current;

        if (current is null)
        {
            info = null;
            return false;
        }

        current.Value.Cts.Cancel();
        info = current.Value.Info;
        return true;
    }

    private void End()
    {
        lock (_gate) _current = null;
    }

    private sealed class Scope(ActiveTurnTracker tracker) : IDisposable
    {
        public void Dispose() => tracker.End();
    }
}
