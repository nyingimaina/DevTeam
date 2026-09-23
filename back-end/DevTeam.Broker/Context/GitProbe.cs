using System.Globalization;

using DevTeam.Broker.Gates;

using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Context;

/// <summary>Read-only git facts via <see cref="IProcessRunner"/>. Swallows every failure into a
/// "not a repo / unknown" result rather than throwing, because indexing is best-effort.</summary>
public sealed class GitProbe : IGitProbe
{
    private const int GitTimeoutMs = 15_000;

    private readonly IProcessRunner _runner;
    private readonly ILogger<GitProbe>? _logger;

    public GitProbe(IProcessRunner runner, ILogger<GitProbe>? logger = null)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<GitProbeResult> DescribeAsync(string workspacePath, CancellationToken ct)
    {
        var inRepo = await RunAsync(workspacePath, "rev-parse --is-inside-work-tree", ct);
        if (inRepo is null || inRepo.ExitCode != 0 || !inRepo.StandardOutput.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return new GitProbeResult(false, null, null, false);

        var head = await RunAsync(workspacePath, "rev-parse HEAD", ct);
        var branch = await RunAsync(workspacePath, "rev-parse --abbrev-ref HEAD", ct);
        var status = await RunAsync(workspacePath, "status --porcelain", ct);

        return new GitProbeResult(
            true,
            head?.ExitCode == 0 ? head.StandardOutput.Trim() : null,
            branch?.ExitCode == 0 ? branch.StandardOutput.Trim() : null,
            status is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(status.StandardOutput));
    }

    public async Task<int?> CountCommitsAsync(string workspacePath, string fromCommit, CancellationToken ct)
    {
        var result = await RunAsync(workspacePath, $"rev-list --count \"{fromCommit}..HEAD\"", ct);
        if (result is null || result.ExitCode != 0)
            return null;

        return int.TryParse(result.StandardOutput.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    private async Task<ProcessRunResult?> RunAsync(string workspacePath, string arguments, CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(
                new ProcessRunRequest("git", $"-C {Quote(workspacePath)} {arguments}", workspacePath, GitTimeoutMs),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // git not installed / not on PATH → "not a repo" is the honest answer, not a crash.
            _logger?.LogDebug(ex, "git probe failed for {Workspace}", workspacePath);
            return null;
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";
}
