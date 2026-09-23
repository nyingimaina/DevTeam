namespace DevTeam.Broker.Server;

/// <summary>What's currently occupying the broker's single agent-turn slot.</summary>
public sealed record ActiveTurnInfo(
    Guid SessionId,
    string AcpSessionId,
    string Preview,
    DateTimeOffset StartedAt,
    bool IsPriming = false,
    // Heartbeat: when the agent last produced anything. Lets the UI say "quiet for 2 minutes"
    // honestly, instead of guessing from "no turn present" — which read as "hung".
    DateTimeOffset? LastEventAt = null,
    // The most recent activity, oldest first (bounded — see ActiveTurnTracker.ActivityCapacity).
    IReadOnlyList<TurnActivityEntry>? Activity = null,
    // How many prompts are currently queued for the broker's single turn slot, this one
    // included. Lets the UI say "one step ahead of you" instead of an open-ended "waiting".
    int QueuedTurns = 0)
{
    /// <summary>
    /// The DevTeamSession id, named for the thing the UI actually correlates it against:
    /// <c>ReleaseStageRun.AcpSessionId</c> — which, despite its name, stores this same
    /// DevTeamSession id (see WorkflowEngine). Comparing against <see cref="AcpSessionId"/>
    /// (the *real* ACP session id) never matches, which made the UI claim a run was queued
    /// behind another release while that very run was executing.
    /// </summary>
    public Guid StageRunSessionId => SessionId;
}

/// <summary>
/// Tracks whichever agent turn currently holds <c>BrokerCoordinator</c>'s turn lock, so
/// the UI can show what's running (or stuck) and cancel it — the broker drives one
/// opencode process shared by every release, so only one turn is ever active at a time.
/// Also holds the turn's recent activity (in memory, never persisted) so the UI can show what
/// the agent is actually doing instead of a single static "running" line.
/// </summary>
public sealed class ActiveTurnTracker
{
    /// <summary>How many activity entries are kept for the live feed. A turn can emit thousands
    /// of chunks; the UI only ever shows a screenful.</summary>
    public const int ActivityCapacity = 100;

    private readonly object _gate = new();
    private Turn? _current;
    private int _queued;

    public ActiveTurnInfo? Current
    {
        get
        {
            lock (_gate)
            {
                return _current is null ? null : _current.Snapshot(_queued);
            }
        }
    }

    /// <summary>
    /// Marks this caller as queued for the single turn slot, until the returned scope is
    /// disposed (which callers do the moment they acquire the slot). Purely observational.
    /// </summary>
    public IDisposable BeginQueued()
    {
        lock (_gate) _queued++;
        return new QueuedScope(this);
    }

    /// <summary>Marks a turn as active until the returned scope is disposed.</summary>
    public IDisposable Begin(
        Guid sessionId, string acpSessionId, string text, CancellationTokenSource cts, bool isPriming = false)
    {
        var preview = text.Length > 80 ? text[..80] : text;
        var info = new ActiveTurnInfo(sessionId, acpSessionId, preview, DateTimeOffset.UtcNow, isPriming);

        lock (_gate)
        {
            _current = new Turn(info, cts);
            _current.Add(new TurnActivityEntry(
                info.StartedAt, TurnActivityKind.Status, isPriming ? "The agent started working" : "The agent is replying"));
        }

        return new Scope(this);
    }

    /// <summary>
    /// Appends one plain-language activity entry to the running turn and refreshes the heartbeat.
    /// A no-op when no turn is active (events can arrive just after a turn ends).
    /// </summary>
    public void Record(string kind, string label, string? detail = null, string? status = null)
    {
        lock (_gate)
        {
            _current?.Add(new TurnActivityEntry(DateTimeOffset.UtcNow, kind, label, detail, status));
        }
    }

    /// <summary>
    /// Refreshes the heartbeat without adding a feed entry. Every inbound frame is proof the
    /// agent is still alive, but only some kinds belong in the activity feed — a usage tick
    /// keeps the turn out of the "stalled" bucket without cluttering what the user reads.
    /// </summary>
    public void Touch()
    {
        lock (_gate)
        {
            _current?.Touch();
        }
    }

    /// <summary>Cancels the active turn's token, if any. Returns what was cancelled.</summary>
    public bool TryCancelCurrent(out ActiveTurnInfo? info)
    {
        Turn? current;
        lock (_gate) current = _current;

        if (current is null)
        {
            info = null;
            return false;
        }

        current.Cts.Cancel();
        info = current.Snapshot(0);
        return true;
    }

    private void End()
    {
        lock (_gate) _current = null;
    }

    private void EndQueued()
    {
        lock (_gate) _queued = Math.Max(0, _queued - 1);
    }

    /// <summary>Mutable per-turn state; only ever touched under <see cref="_gate"/>.</summary>
    private sealed class Turn(ActiveTurnInfo info, CancellationTokenSource cts)
    {
        private readonly Queue<TurnActivityEntry> _activity = new();

        public ActiveTurnInfo Info { get; } = info;

        public CancellationTokenSource Cts { get; } = cts;

        public DateTimeOffset? LastEventAt { get; private set; }

        public void Add(TurnActivityEntry entry)
        {
            Touch();
            _activity.Enqueue(entry);
            while (_activity.Count > ActivityCapacity)
                _activity.Dequeue();
        }

        public void Touch() => LastEventAt = DateTimeOffset.UtcNow;

        public ActiveTurnInfo Snapshot(int queued)
            => Info with { LastEventAt = LastEventAt, Activity = _activity.ToArray(), QueuedTurns = queued };
    }

    private sealed class Scope(ActiveTurnTracker tracker) : IDisposable
    {
        public void Dispose() => tracker.End();
    }

    private sealed class QueuedScope(ActiveTurnTracker tracker) : IDisposable
    {
        public void Dispose() => tracker.EndQueued();
    }
}
