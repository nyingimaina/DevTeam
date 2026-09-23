namespace DevTeam.Broker.Context;

/// <summary>How a Repomix invocation ended. The caller maps each to a best-effort behaviour.</summary>
public enum RepomixOutcome
{
    Success,
    Missing,
    TimedOut,
    Failed,
}

public sealed record RepomixResult(
    RepomixOutcome Outcome,
    string OutputPath,
    long Bytes,
    string StandardOutput,
    string StandardError)
{
    public bool Success => Outcome == RepomixOutcome.Success;

    /// <summary>First 2 KB of stderr — enough to explain a failure without logging a wall of text.</summary>
    public string StderrPreview => StandardError.Length <= 2048 ? StandardError : StandardError[..2048];
}

/// <summary>
/// Thin wrapper over <c>IProcessRunner</c> so Repomix is fakeable in tests. One method, one job:
/// run Repomix for a workspace and report what happened (never throw for a process failure).
/// </summary>
public interface IRepomixRunner
{
    Task<RepomixResult> RunAsync(
        string workspacePath,
        string outputPath,
        bool compress,
        IReadOnlyList<string> ignore,
        TimeSpan timeout,
        CancellationToken ct);
}
