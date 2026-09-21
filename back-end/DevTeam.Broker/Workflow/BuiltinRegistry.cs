namespace DevTeam.Broker.Workflow;

public static class BuiltinRegistry
{
    public const string ScaffoldSpecs = "scaffold_specs";
    public const string ContextBundle = "context_bundle";
    public const string GherkinValidator = "gherkin_validator";
    public const string VerifyCode = "verify_code";
    public const string CodeHygiene = "code_hygiene";
    public const string SliceGuard = "slice_guard";
    public const string SliceScope = "slice_scope";
    public const string ReuseGate = "reuse_gate";
    public const string RenderPr = "render_pr";
    public const string RenderHandoff = "render_handoff";
    public const string CoverageMatrix = "coverage_matrix";

    public static IReadOnlySet<string> All => new HashSet<string>
    {
        ScaffoldSpecs,
        ContextBundle,
        GherkinValidator,
        VerifyCode,
        CodeHygiene,
        SliceGuard,
        SliceScope,
        ReuseGate,
        RenderPr,
        RenderHandoff,
        CoverageMatrix,
    };

    public static bool IsKnown(string name) => All.Contains(name);
}