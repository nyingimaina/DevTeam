namespace DevTeam.Broker.Gates;

/// <summary>
/// The feature's changed-file set, straight from git. Shared by the gates that need to reason
/// about "what did this feature actually touch": the fast lane (verify only those files) and the
/// test report (a test file changed without an approved challenge is a silent rewrite).
/// </summary>
public sealed class GitChangeSet
{
    private const int TimeoutMs = 60_000;

    private readonly IProcessRunner _runner;

    public GitChangeSet(IProcessRunner runner) => _runner = runner;

    /// <summary>
    /// Files changed since <paramref name="baseRef"/> (when given) plus everything not yet
    /// committed - the latter is what the developer stage is actually working on when the gates
    /// run mid-loop, so a base ref alone would miss the edit just made.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChangedPathsAsync(
        string workspacePath, string? baseRef, CancellationToken cancellationToken)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(IEnumerable<string> discovered)
        {
            foreach (var path in discovered)
            {
                if (seen.Add(path))
                    paths.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(baseRef))
        {
            var committed = await RunAsync($"diff --name-only {baseRef}...HEAD", workspacePath, cancellationToken);
            Add(ChangedFiles.Parse(committed.StandardOutput));
        }

        var working = await RunAsync("status --porcelain", workspacePath, cancellationToken);
        Add(ChangedFiles.ParsePorcelain(working.StandardOutput));
        return paths;
    }

    private Task<ProcessRunResult> RunAsync(string arguments, string workspacePath, CancellationToken cancellationToken)
        => _runner.RunAsync(new ProcessRunRequest("git", arguments, workspacePath, TimeoutMs), cancellationToken);
}
