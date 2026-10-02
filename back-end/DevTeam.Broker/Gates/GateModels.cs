namespace DevTeam.Broker.Gates;

public sealed record GateRequest(
    string Builtin,
    string WorkspacePath,
    string? FeatureKey = null,
    string? RoleName = null,
    IReadOnlyDictionary<string, string>? Inputs = null);

/// <summary>
/// Who must act when a gate fails. Routing used to follow only a step's static ResponsibleRole,
/// which sent report defects and pending operator rulings to a developer who could do nothing
/// about them - the failure came straight back. The gate that knows why it failed says so.
/// </summary>
public enum FailureKind
{
    /// <summary>A code defect (or a check with no opinion): the step's ResponsibleRole owns it.</summary>
    Default = 0,

    /// <summary>The stage's own output is defective: the same stage repairs it; nobody upstream can.</summary>
    SameStage,

    /// <summary>Only the operator can unblock it. Never retried or routed automatically.</summary>
    OperatorDecision,
}

public sealed record GateResult(
    bool Passed,
    string Reason,
    string EvidenceText = "",
    string? ArtifactPath = null)
{
    public FailureKind Kind { get; init; }

    public GateResult OfKind(FailureKind kind) => this with { Kind = kind };

    public static GateResult Fail(string reason, string evidence = "", string? artifactPath = null)
        => new(false, reason, evidence, artifactPath);

    public static GateResult Pass(string reason, string evidence = "", string? artifactPath = null)
        => new(true, reason, evidence, artifactPath);
}

public interface IGate
{
    string Name { get; }

    Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessRunRequest(
    string FileName,
    string Arguments,
    string WorkingDirectory,
    int TimeoutMs = 300_000);

public sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    TimeSpan Duration);

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken);
}