namespace DevTeam.Broker.Context;

/// <summary>
/// The code-overview feature's one public surface. Every method is best-effort: none of them may
/// throw because of indexing trouble (spec REQ-007), so callers can fire and forget safely.
/// </summary>
public interface IRepoContextService
{
    /// <summary>False makes every entry point a no-op (spec REQ-012).</summary>
    bool Enabled { get; }

    /// <summary>Queue a refresh. Never throws, never blocks on the build.</summary>
    void EnqueueRefresh(string workspacePath, string trigger);

    /// <summary>Current overview state for a workspace, suitable for the status endpoint.</summary>
    Task<RepoContextStatus> GetStatusAsync(string workspacePath, CancellationToken ct);

    /// <summary>
    /// Build the overview now. Public so tests (and the worker) can drive it directly; the
    /// completion hook only ever enqueues.
    /// </summary>
    Task<RepoContextRefreshResult> RefreshAsync(string workspacePath, string trigger, CancellationToken ct);
}
