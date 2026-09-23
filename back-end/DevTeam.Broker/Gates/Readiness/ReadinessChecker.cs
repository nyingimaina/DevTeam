using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Gates.Readiness;

/// <summary>
/// Runs a workspace's readiness profile and reports the outcome. This is the C# replacement for
/// the old verify.ps1: same phases, same verdict, but structured results and no external script.
/// </summary>
public interface IReadinessChecker
{
    Task<ReadinessReport> CheckAsync(
        string workspacePath, ReadinessScope scope, string? featureKey, CancellationToken cancellationToken);
}

/// <summary>Facts about the machine a run depends on — injected so the runner is testable.</summary>
public interface IReadinessEnvironment
{
    bool IsDatabaseReachable();
}

/// <summary>
/// Wraps a command line so it runs through the platform shell. Readiness commands are written the
/// way a person would type them ("npx jest --coverage", with quoted arguments) — running them
/// directly would miss Windows' npm/npx .cmd shims and mishandle the quotes.
/// </summary>
public static class ShellCommand
{
    public static ProcessRunRequest Create(string commandLine, string workingDirectory, int timeoutMs)
        => OperatingSystem.IsWindows()
            ? new ProcessRunRequest("cmd.exe", "/d /c " + commandLine, workingDirectory, timeoutMs)
            : new ProcessRunRequest("/bin/sh", "-c \"" + commandLine.Replace("\"", "\\\"") + "\"", workingDirectory, timeoutMs);
}

public sealed class ReadinessChecker : IReadinessChecker
{
    private readonly IProcessRunner _runner;
    private readonly IReadinessEnvironment _environment;
    private readonly ILogger<ReadinessChecker>? _logger;

    public ReadinessChecker(IProcessRunner runner, IReadinessEnvironment environment, ILogger<ReadinessChecker>? logger = null)
    {
        _runner = runner;
        _environment = environment;
        _logger = logger;
    }

    public Task<ReadinessReport> CheckAsync(
        string workspacePath, ReadinessScope scope, string? featureKey, CancellationToken cancellationToken)
        => RunPhasesAsync(ReadinessProfileLoader.Load(workspacePath), workspacePath, scope, featureKey, cancellationToken);

    public async Task<ReadinessReport> RunPhasesAsync(
        ReadinessProfile profile,
        string workspacePath,
        ReadinessScope scope,
        string? featureKey,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        // Phases in different DependsOn-connected components (e.g. the backend build/test chain
        // and the frontend typecheck/build/lint/test chain) share no ordering constraint, so
        // there's no reason to pay for them back-to-back — run each independent group
        // concurrently, preserving dependency order only within a group.
        var groups = GroupIntoIndependentSubgraphs(profile.Phases);
        var groupResults = await Task.WhenAll(
            groups.Select(group => RunGroupSequentiallyAsync(group, workspacePath, cancellationToken)));
        var byId = groupResults.SelectMany(g => g).ToDictionary(r => r.Id, StringComparer.Ordinal);
        // The profile's own declared order, not whichever group happened to finish first — the
        // report should read the same regardless of execution timing.
        var results = profile.Phases.Select(phase => byId[phase.Id]).ToList();
        var requiredFailed = profile.Phases.Any(phase =>
            phase.Required && byId[phase.Id].Status == ReadinessCheckStatus.Failed);

        watch.Stop();
        _logger?.LogInformation(
            "Final checks for {Scope} {FeatureKey} finished in {ElapsedMs}ms: passed={Passed}, failed={Failed}, skipped={Skipped}",
            scope, featureKey ?? "(none)", watch.ElapsedMilliseconds,
            results.Count(r => r.Status == ReadinessCheckStatus.Passed),
            results.Count(r => r.Status == ReadinessCheckStatus.Failed),
            results.Count(r => r.Status == ReadinessCheckStatus.Skipped));

        return new ReadinessReport(
            workspacePath, scope, featureKey, !requiredFailed, startedAt, watch.ElapsedMilliseconds, results);
    }

    private async Task<List<ReadinessPhaseResult>> RunGroupSequentiallyAsync(
        List<ReadinessPhaseDefinition> group, string workspacePath, CancellationToken cancellationToken)
    {
        var results = new List<ReadinessPhaseResult>();
        // Local to this group: a phase can only DependsOn another phase in the same connected
        // component (that's how the grouping itself is computed), so no cross-group lookups are
        // ever needed here — each group's dictionary never has to be shared or synchronized.
        var byId = new Dictionary<string, ReadinessPhaseResult>(StringComparer.Ordinal);
        foreach (var phase in group)
        {
            var result = RunOrLog(await RunPhaseAsync(phase, byId, workspacePath, cancellationToken), phase);
            results.Add(result);
            byId[phase.Id] = result;
        }
        return results;
    }

    // Union-find over DependsOn edges: two phases land in the same group iff one (transitively)
    // depends on the other. Groups with no edges between them have zero ordering constraint and
    // are safe to run concurrently.
    private static List<List<ReadinessPhaseDefinition>> GroupIntoIndependentSubgraphs(
        IReadOnlyList<ReadinessPhaseDefinition> phases)
    {
        var parent = phases.ToDictionary(p => p.Id, p => p.Id, StringComparer.Ordinal);

        string Find(string id)
        {
            while (parent[id] != id)
            {
                parent[id] = parent[parent[id]];
                id = parent[id];
            }
            return id;
        }

        void Union(string a, string b)
        {
            var rootA = Find(a);
            var rootB = Find(b);
            if (rootA != rootB)
                parent[rootA] = rootB;
        }

        foreach (var phase in phases)
        {
            foreach (var dependsOnId in phase.DependsOn ?? [])
            {
                if (parent.ContainsKey(dependsOnId))
                    Union(phase.Id, dependsOnId);
            }
        }

        // GroupBy preserves each phase's original relative order within its group, which is what
        // keeps a dependency chain (e.g. build -> unit -> integration) running in the right order.
        return phases.GroupBy(p => Find(p.Id)).Select(g => g.ToList()).ToList();
    }

    private ReadinessPhaseResult RunOrLog(ReadinessPhaseResult result, ReadinessPhaseDefinition phase)
    {
        if (_logger is null)
            return result;

        switch (result.Status)
        {
            case ReadinessCheckStatus.Passed:
                _logger.LogDebug(
                    "Check '{Check}' passed in {ElapsedMs}ms: {Command}",
                    phase.Id, result.DurationMs, phase.Command);
                break;
            case ReadinessCheckStatus.Skipped:
                _logger.LogInformation("Check '{Check}' skipped: {Reason}", phase.Id, result.Reason);
                break;
            default:
                _logger.LogWarning(
                    "Check '{Check}' FAILED in {ElapsedMs}ms: {Command} — {Reason}\n--- output (tail) ---\n{Output}",
                    phase.Id, result.DurationMs, phase.Command, result.Reason, result.RawOutput);
                break;
        }

        return result;
    }

    private async Task<ReadinessPhaseResult> RunPhaseAsync(
        ReadinessPhaseDefinition phase,
        IReadOnlyDictionary<string, ReadinessPhaseResult> prior,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        if (BlockedByDependency(phase, prior) is { } failedDependency)
            return Skipped(phase, $"Skipped because “{failedDependency}” didn't pass.");

        if (phase.SkipWhen == ReadinessDefaults.SkipWhenDatabaseUnreachable && !_environment.IsDatabaseReachable())
            return Skipped(phase, "Skipped — no database was available to test against.");

        var workingDirectory = phase.WorkingDirectory is null
            ? workspacePath
            : Path.Combine(workspacePath, phase.WorkingDirectory);

        var watch = Stopwatch.StartNew();
        var run = await _runner.RunAsync(
            ShellCommand.Create(phase.Command, workingDirectory, phase.TimeoutMs), cancellationToken);
        watch.Stop();

        var rawOutput = Tail(StripAnsi(run.StandardOutput) + "\n" + StripAnsi(run.StandardError));
        var metrics = ReadinessMetricsParser.ParseTestOutput(run.StandardOutput + "\n" + run.StandardError);

        if (run.TimedOut)
            return Failure(phase, $"Timed out after {phase.TimeoutMs / 1000} seconds.", watch.ElapsedMilliseconds, metrics, rawOutput);

        if (run.ExitCode != 0)
            return Failure(phase, $"Stopped with an error (exit code {run.ExitCode}).", watch.ElapsedMilliseconds, metrics, rawOutput);

        metrics = WithCoverage(phase, workingDirectory, metrics);
        if (ReadinessMetricsParser.ThresholdProblem(phase, metrics) is { } thresholdProblem)
            return Failure(phase, thresholdProblem, watch.ElapsedMilliseconds, metrics, rawOutput);

        return new ReadinessPhaseResult(
            phase.Id, phase.Title, ReadinessCheckStatus.Passed, "Passed.", watch.ElapsedMilliseconds, metrics, rawOutput);
    }

    private ReadinessMetrics WithCoverage(ReadinessPhaseDefinition phase, string workingDirectory, ReadinessMetrics metrics)
    {
        var spec = phase.Coverage;
        if (spec is null)
            return metrics;

        var path = Path.Combine(workingDirectory, spec.RelativeJsonPath);
        if (!File.Exists(path))
            return metrics;

        try
        {
            var parsed = ReadinessMetricsParser.ParseJestCoverageJson(File.ReadAllText(path));
            return metrics.WithCoverage(parsed.LineCoverage, parsed.BranchCoverage, parsed.FunctionCoverage);
        }
        catch (IOException)
        {
            return metrics;
        }
    }

    private static string? BlockedByDependency(
        ReadinessPhaseDefinition phase, IReadOnlyDictionary<string, ReadinessPhaseResult> prior)
        => (phase.DependsOn ?? [])
            .FirstOrDefault(id => prior.TryGetValue(id, out var result) && result.Status == ReadinessCheckStatus.Failed);

    private static ReadinessPhaseResult Skipped(ReadinessPhaseDefinition phase, string reason)
        => new(phase.Id, phase.Title, ReadinessCheckStatus.Skipped, reason, 0, ReadinessMetrics.Empty, string.Empty);

    private static ReadinessPhaseResult Failure(
        ReadinessPhaseDefinition phase, string reason, long durationMs, ReadinessMetrics metrics, string rawOutput)
        => new(phase.Id, phase.Title, ReadinessCheckStatus.Failed, reason, durationMs, metrics, rawOutput);

    // Keep the tail: build/test failures print their diagnosis last.
    private static string Tail(string output)
        => output.Length <= ReadinessDefaults.MaxRawOutputChars
            ? output
            : "… earlier output trimmed …\n" + output[^ReadinessDefaults.MaxRawOutputChars..];

    // Colours make the stored output unreadable in the report; drop the escape sequences.
    private static string StripAnsi(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, "\u001b\\[[0-9;]*m", string.Empty);
}
