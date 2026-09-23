namespace DevTeam.Broker.Workflow;

public static class BuiltinRegistry
{
    public const string ScaffoldSpecs = "scaffold_specs";
    public const string CoreScaffold = "core_scaffold";
    public const string RepoHygiene = "repo_hygiene";
    public const string ContextBundle = "context_bundle";
    public const string CodeMap = "code_map";
    public const string GherkinValidator = "gherkin_validator";
    public const string BuildCheck = "build_check";
    public const string VerifyCode = "verify_code";
    public const string CodeHygiene = "code_hygiene";
    public const string AppLaunch = "app_launch";
    public const string SliceGuard = "slice_guard";
    public const string SliceScope = "slice_scope";
    public const string ReuseGate = "reuse_gate";
    public const string ProjectStructure = "project_structure";
    public const string RenderPr = "render_pr";
    public const string RenderHandoff = "render_handoff";
    public const string CoverageMatrix = "coverage_matrix";
    public const string FinalChecks = "final_checks";

    public static IReadOnlySet<string> All => new HashSet<string>
    {
        ScaffoldSpecs,
        CoreScaffold,
        RepoHygiene,
        ContextBundle,
        CodeMap,
        GherkinValidator,
        BuildCheck,
        VerifyCode,
        CodeHygiene,
        AppLaunch,
        SliceGuard,
        SliceScope,
        ReuseGate,
        ProjectStructure,
        RenderPr,
        RenderHandoff,
        CoverageMatrix,
        FinalChecks,
    };

    public static bool IsKnown(string name) => All.Contains(name);
}