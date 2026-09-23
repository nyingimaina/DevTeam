using DevTeam.Broker.Gates.Readiness;

namespace DevTeam.Tests.Gates.Readiness;

/// <summary>A readiness checker returning a canned report — used by gate and engine tests.</summary>
public sealed class FakeReadinessChecker : IReadinessChecker
{
    private readonly bool _passed;

    public FakeReadinessChecker(bool passed = true) => _passed = passed;

    public int CallCount { get; private set; }

    public ReadinessScope? LastScope { get; private set; }

    public string? LastFeatureKey { get; private set; }

    public Task<ReadinessReport> CheckAsync(
        string workspacePath, ReadinessScope scope, string? featureKey, CancellationToken cancellationToken)
    {
        CallCount++;
        LastScope = scope;
        LastFeatureKey = featureKey;

        var phase = new ReadinessPhaseResult(
            "backend-build",
            "The backend project builds",
            _passed ? ReadinessCheckStatus.Passed : ReadinessCheckStatus.Failed,
            _passed ? "Passed." : "Stopped with an error (exit code 1).",
            10,
            ReadinessMetrics.Empty,
            "sample output");

        return Task.FromResult(new ReadinessReport(
            workspacePath, scope, featureKey, _passed, DateTimeOffset.UtcNow, 10, [phase]));
    }
}
