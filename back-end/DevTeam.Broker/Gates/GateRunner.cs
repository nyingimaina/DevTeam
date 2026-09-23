using DevTeam.Broker.Context;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Workflow;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Gates;

public sealed class UnknownGateException : InvalidOperationException
{
    public UnknownGateException(string builtin)
        : base($"No builtin gate is registered for '{builtin}'. Known builtins: {string.Join(", ", BuiltinRegistry.All.OrderBy(x => x))}")
    {
    }
}

public interface IGateRunner
{
    Task<GateResult> RunAsync(string builtin, GateRequest request, CancellationToken cancellationToken);
}

public sealed class GateRunner : IGateRunner
{
    private const int EvidencePreviewChars = 2000;

    private readonly IReadOnlyDictionary<string, IGate> _gates;
    private readonly ILogger<GateRunner>? _logger;

    public GateRunner(IGate[] gates, ILogger<GateRunner>? logger = null)
    {
        _gates = gates.ToDictionary(gate => gate.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    public async Task<GateResult> RunAsync(string builtin, GateRequest request, CancellationToken cancellationToken)
    {
        if (!_gates.TryGetValue(builtin, out var gate))
            throw new UnknownGateException(builtin);

        // Every built-in check runs through here, so this is the one place that records what was
        // checked, how long it took, and why it failed — the questions a support specialist
        // actually asks ("which check? how long? what did it say?").
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _logger?.LogDebug(
            "Check '{Check}' starting (feature {FeatureKey}, role {Role}, workspace {Workspace})",
            builtin, request.FeatureKey, request.RoleName, request.WorkspacePath);

        try
        {
            var result = await gate.RunAsync(request, cancellationToken);
            stopwatch.Stop();

            if (result.Passed)
            {
                _logger?.LogDebug(
                    "Check '{Check}' passed in {ElapsedMs}ms (feature {FeatureKey})",
                    builtin, stopwatch.ElapsedMilliseconds, request.FeatureKey);
            }
            else
            {
                _logger?.LogWarning(
                    "Check '{Check}' FAILED in {ElapsedMs}ms (feature {FeatureKey}, role {Role}): {Reason}\n--- evidence ---\n{Evidence}",
                    builtin, stopwatch.ElapsedMilliseconds, request.FeatureKey, request.RoleName,
                    result.Reason, Truncate(result.EvidenceText));
            }

            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger?.LogError(ex,
                "Check '{Check}' threw after {ElapsedMs}ms (feature {FeatureKey}, workspace {Workspace})",
                builtin, stopwatch.ElapsedMilliseconds, request.FeatureKey, request.WorkspacePath);
            throw;
        }
    }

    private static string Truncate(string? text)
        => text is null ? string.Empty : text.Length <= EvidencePreviewChars ? text : text[..EvidencePreviewChars] + "\n…(truncated)";
}

public sealed class BuiltinGateRegistry
{
    public static IGate[] Create(IProcessRunner runner, IReadinessChecker readiness, IRepoContextService? repoContext = null) =>
    [
        new ScaffoldSpecsGate(),
        new CoreScaffoldGate(),
        new RepoHygieneGate(runner),
        new ContextBundleGate(),
        new CodeMapGate(repoContext),
        new GherkinValidatorGate(),
        new BuildCheckGate(runner),
        new VerifyCodeGate(runner),
        new CodeHygieneGate(runner),
        new AppLaunchGate(runner),
        new SliceGuardGate(runner),
        new SliceScopeGate(runner),
        new ReuseGate(),
        new ProjectStructureGate(),
        new CoverageMatrixGate(),
        new RenderPrGate(runner),
        new RenderHandoffGate(),
        new FinalChecksGate(readiness),
    ];
}