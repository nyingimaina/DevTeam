using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// The per-feature final gate: runs the workspace's strict readiness profile (build, tests,
/// type-check, house rules) and passes only when none of the required checks failed. Its
/// evidence is the plain-language summary the Checks library shows.
/// </summary>
public sealed class FinalChecksGate : IGate
{
    private readonly IReadinessChecker _checker;

    public FinalChecksGate(IReadinessChecker checker) => _checker = checker;

    public string Name => BuiltinRegistry.FinalChecks;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var report = await _checker.CheckAsync(
            request.WorkspacePath, ReadinessScope.Feature, request.FeatureKey, cancellationToken);

        var evidence = ReadinessSummaryText.Describe(report);
        return report.Passed
            ? GateResult.Pass("Every final check passed.", evidence)
            : GateResult.Fail("Some final checks didn't pass.", evidence);
    }
}
