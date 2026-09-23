using DevTeam.Broker.Context;
using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Keeps the workspace's git housekeeping in order, before any agent works in it: a comprehensive
/// <c>.gitignore</c> (merge-safe), the local <c>devteam/context/</c> exclude, and untracking any
/// file that is ignored but still tracked. The last one is what stops build output and logs from
/// being committed and then re-read (and paid for) by later stages. Best-effort: it always passes,
/// and reports what it changed.
/// </summary>
public sealed class RepoHygieneGate : IGate
{
    private const int GitTimeoutMs = 30_000;

    private readonly IProcessRunner _runner;
    private readonly bool _untrackIgnored;

    public RepoHygieneGate(IProcessRunner runner, bool untrackIgnored = true)
    {
        _runner = runner;
        _untrackIgnored = untrackIgnored;
    }

    public string Name => BuiltinRegistry.RepoHygiene;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.WorkspacePath) || !Directory.Exists(request.WorkspacePath))
            return GateResult.Pass("No workspace to tidy", "workspace folder not found");

        var notes = new List<string>();

        if (EnsureGitIgnore(request.WorkspacePath, notes))
            notes.Add("added missing entries to .gitignore");

        if (RepoContextStore.EnsureGitExcluded(request.WorkspacePath))
            notes.Add("excluded devteam/context from git");

        if (_untrackIgnored)
        {
            var untracked = await UntrackIgnoredFilesAsync(request.WorkspacePath, cancellationToken);
            if (untracked.Count > 0)
                notes.Add($"untracked {untracked.Count} ignored file(s): {string.Join(", ", untracked.Take(10))}");
        }

        return GateResult.Pass(
            "Project housekeeping is in place",
            notes.Count == 0 ? "nothing to change" : string.Join("; ", notes),
            ".gitignore");
    }

    private static bool EnsureGitIgnore(string workspacePath, List<string> notes)
    {
        var path = Path.Combine(workspacePath, ".gitignore");
        string? existing = null;
        try
        {
            existing = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return false;
        }

        var merged = GitIgnoreTemplate.Merge(existing);
        if (existing is not null && string.Equals(existing, merged, StringComparison.Ordinal))
            return false;

        try
        {
            File.WriteAllText(path, merged);
            return true;
        }
        catch (IOException)
        {
            notes.Add("could not write .gitignore");
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> UntrackIgnoredFilesAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            var listed = await _runner.RunAsync(
                new ProcessRunRequest("git", "ls-files -i -c --exclude-standard", workspacePath, GitTimeoutMs), ct);
            if (listed.ExitCode != 0 || string.IsNullOrWhiteSpace(listed.StandardOutput))
                return [];

            var paths = listed.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0)
                .ToArray();
            if (paths.Length == 0)
                return [];

            var arguments = "rm --cached -r --ignore-unmatch -- " +
                            string.Join(' ', paths.Select(Quote));
            var removed = await _runner.RunAsync(
                new ProcessRunRequest("git", arguments, workspacePath, GitTimeoutMs), ct);

            return removed.ExitCode == 0 ? paths : [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // No git, a locked index, anything — tidying is best-effort and never blocks work.
            return [];
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
}
