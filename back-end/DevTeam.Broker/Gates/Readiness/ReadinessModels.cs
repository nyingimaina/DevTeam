namespace DevTeam.Broker.Gates.Readiness;

/// <summary>Outcome of one readiness phase — see <see cref="ReadinessPhaseResult"/>.</summary>
public enum ReadinessCheckStatus
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>Whether a report verifies a single feature's work or the whole release before it ships.</summary>
public enum ReadinessScope
{
    Feature,
    Release,
}

public static class ReadinessDefaults
{
    // `next build` + `dotnet test` on a cold cache comfortably exceed the generic 5-minute
    // gate budget (GateModels.ProcessRunRequest), so readiness phases get their own, longer
    // ceiling by default — still overridable per phase.
    public const int PhaseTimeoutMs = 15 * 60 * 1000;

    // Raw stdout/stderr is persisted for the in-app report; cap it so a runaway build log
    // can't bloat the database (the tail is what matters — it holds the summary/error).
    public const int MaxRawOutputChars = 64 * 1024;

    // A phase whose SkipWhen is this is skipped (recorded, never blocking) when the local
    // database isn't reachable — matching the old verify.ps1 behaviour.
    public const string SkipWhenDatabaseUnreachable = "database-unreachable";
}

/// <summary>
/// A phase can point at a coverage report the command produced; when present, the file is
/// parsed after the command passes and any configured minimum is enforced.
/// </summary>
public sealed record ReadinessCoverageSpec(
    string RelativeJsonPath,
    double? LineMin = null,
    double? BranchMin = null,
    double? FunctionMin = null);

/// <summary>
/// One strict check the ship-readiness gate must run. <see cref="DependsOn"/> makes a phase
/// skip (rather than report a misleading failure) when an earlier phase it relies on failed —
/// e.g. don't try to build when type-checking already failed.
/// </summary>
public sealed record ReadinessPhaseDefinition(
    string Id,
    string Title,
    string Command,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? DependsOn = null,
    int TimeoutMs = ReadinessDefaults.PhaseTimeoutMs,
    bool Required = true,
    string? SkipWhen = null,
    ReadinessCoverageSpec? Coverage = null);

/// <summary>Numbers extracted from a phase's output, for charts and trend lines.</summary>
public sealed record ReadinessMetrics(
    int? TestsPassed = null,
    int? TestsFailed = null,
    int? TestsSkipped = null,
    double? LineCoverage = null,
    double? BranchCoverage = null,
    double? FunctionCoverage = null)
{
    public static readonly ReadinessMetrics Empty = new();

    public ReadinessMetrics WithCoverage(double? line, double? branch, double? functions) => this with
    {
        LineCoverage = line ?? LineCoverage,
        BranchCoverage = branch ?? BranchCoverage,
        FunctionCoverage = functions ?? FunctionCoverage,
    };

    public bool HasCoverage => LineCoverage is not null || BranchCoverage is not null || FunctionCoverage is not null;
}

public sealed record ReadinessPhaseResult(
    string Id,
    string Title,
    ReadinessCheckStatus Status,
    string Reason,
    long DurationMs,
    ReadinessMetrics Metrics,
    string RawOutput);

/// <summary>
/// The full result of one readiness run. <see cref="Passed"/> is true unless a *required*
/// phase failed — a skipped phase is recorded but never blocks (see ReadinessCheckStatus).
/// </summary>
public sealed record ReadinessReport(
    string WorkspacePath,
    ReadinessScope Scope,
    string? FeatureKey,
    bool Passed,
    DateTimeOffset StartedAt,
    long DurationMs,
    IReadOnlyList<ReadinessPhaseResult> Phases)
{
    public int PassedCount => Phases.Count(p => p.Status == ReadinessCheckStatus.Passed);

    public int FailedCount => Phases.Count(p => p.Status == ReadinessCheckStatus.Failed);

    public int SkippedCount => Phases.Count(p => p.Status == ReadinessCheckStatus.Skipped);

    public IReadOnlyList<ReadinessPhaseResult> Failures =>
        Phases.Where(p => p.Status == ReadinessCheckStatus.Failed).ToList();
}
