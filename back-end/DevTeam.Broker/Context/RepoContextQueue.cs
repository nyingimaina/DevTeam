namespace DevTeam.Broker.Context;

public sealed record RepoContextWork(string WorkspacePath, string Trigger);

/// <summary>
/// A bounded, coalescing work queue: one pending refresh per workspace. Two completions arriving
/// close together collapse into a single later run (spec REQ-005, "coalesce"), and the queue
/// holds only the two strings the refresh needs — never database entities (§12.2).
/// </summary>
public sealed class RepoContextQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Path, string Trigger)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);

    public void Enqueue(string workspacePath, string trigger)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
            return;

        lock (_gate)
            _pending[Normalize(workspacePath)] = (workspacePath, trigger);

        _signal.Release();
    }

    /// <summary>Returns the next pending refresh, or null when the queue raced empty.</summary>
    public async Task<RepoContextWork?> DequeueAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);

        lock (_gate)
        {
            if (_pending.Count == 0)
                return null;

            var key = _pending.Keys.First();
            var (path, trigger) = _pending[key];
            _pending.Remove(key);
            return new RepoContextWork(path, trigger);
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    public bool IsPending(string workspacePath)
    {
        lock (_gate)
            return _pending.ContainsKey(Normalize(workspacePath));
    }

    public static string Normalize(string workspacePath)
        => workspacePath.Replace('\\', '/').TrimEnd('/');
}
