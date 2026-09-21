using DevTeam.Broker.Workflow;

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
    private readonly IReadOnlyDictionary<string, IGate> _gates;

    public GateRunner(IGate[] gates)
    {
        _gates = gates.ToDictionary(gate => gate.Name, StringComparer.Ordinal);
    }

    public Task<GateResult> RunAsync(string builtin, GateRequest request, CancellationToken cancellationToken)
    {
        if (!_gates.TryGetValue(builtin, out var gate))
            throw new UnknownGateException(builtin);
        return gate.RunAsync(request, cancellationToken);
    }
}

public sealed class BuiltinGateRegistry
{
    public static IGate[] Create(IProcessRunner runner) =>
    [
        new ScaffoldSpecsGate(),
        new ContextBundleGate(),
        new GherkinValidatorGate(),
        new VerifyCodeGate(runner),
        new CodeHygieneGate(runner),
        new SliceGuardGate(runner),
        new SliceScopeGate(runner),
        new ReuseGate(),
        new CoverageMatrixGate(),
        new RenderPrGate(runner),
        new RenderHandoffGate(),
    ];
}