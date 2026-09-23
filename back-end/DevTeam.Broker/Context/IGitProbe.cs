namespace DevTeam.Broker.Context;

/// <summary>The handful of git facts the code-overview feature needs, in one read.</summary>
public sealed record GitProbeResult(bool IsRepo, string? Head, string? Branch, bool Dirty);

/// <summary>
/// Git facts via <c>IProcessRunner</c> rather than <c>IGitService</c>: the code-overview feature
/// only ever needs read-only facts, and adding methods to <c>IGitService</c> would break every
/// fake in the test suite (spec §12.10). Kept deliberately tiny so it is trivial to fake.
/// </summary>
public interface IGitProbe
{
    Task<GitProbeResult> DescribeAsync(string workspacePath, CancellationToken ct);

    /// <summary>
    /// How many commits <paramref name="fromCommit"/> is behind HEAD. Null when the count can't
    /// be computed (commit gone after a rebase/force-push, or git unavailable).
    /// </summary>
    Task<int?> CountCommitsAsync(string workspacePath, string fromCommit, CancellationToken ct);
}
