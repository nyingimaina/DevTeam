namespace DevTeam.Broker.Gates;

public sealed record GateRequest(
    string Builtin,
    string WorkspacePath,
    string? FeatureKey = null,
    string? RoleName = null,
    IReadOnlyDictionary<string, string>? Inputs = null);

public sealed record GateResult(
    bool Passed,
    string Reason,
    string EvidenceText = "",
    string? ArtifactPath = null)
{
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