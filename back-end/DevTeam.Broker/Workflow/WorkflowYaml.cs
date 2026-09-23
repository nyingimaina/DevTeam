using DevTeam.Broker.Gates;
using YamlDotNet.Serialization;

namespace DevTeam.Broker.Workflow;

public sealed class WorkflowYaml
{
    public bool Opinionated { get; set; } = true;

    // A workspace-wide (not per-feature) folder for cross-cutting docs artifacts — see
    // ArtifactRoots.DocsRoot / WorkflowArtifact.
    public string DocsRoot { get; set; } = "docs";

    public ReleaseYaml Release { get; set; } = new();

    public SlicesYaml Slices { get; set; } = new();

    public Dictionary<string, RoleYaml> Pipeline { get; set; } = DefaultPipeline();

    public List<ChallengeYaml> Challenges { get; set; } = DefaultChallenges();

    internal static Dictionary<string, RoleYaml> DefaultPipeline() => new()
    {
        ["business-analyst"] = new RoleYaml
        {
            Signoff = Signoffs.RequirementsApproval,
            UserInputRequired = true,
            SeedPrompt =
                " Early on, ask about the app type (e.g. web API, desktop/WPF, console, library) and the file and folder " +
                "hierarchy the code should live in — don't assume a backend/frontend split; a single-project desktop or " +
                "console app has no \"frontend\" at all, and forcing one on it just creates an unused folder the developer " +
                "has to clean up later. The feature's manifest.yaml already exists (scaffolded with a generic default); " +
                "once you know the real app type and hierarchy, update devteam/features/<F>/manifest.yaml directly with " +
                "your file tools — set its codePaths list to the actual folders this feature's code and tests belong " +
                "under (e.g. [\"src/Features/<F>\", \"src/Features/<F>.Tests\"] for a single-project app), replacing the " +
                "generic backend/frontend default. " +
                "Author the agreed requirements into devteam/features/<F>/" + ArtifactPaths.BrsFileName + " " +
                "(the BRS — Business Requirements Specification) using your file tools (create the directory if needed): " +
                "one \"## REQ-N: <Title>\" section per requirement, " +
                "each followed by a Given/When/Then acceptance-criteria sentence. Every requirement MUST contain Given, When and Then. " +
                "Use the exact id form \"REQ-<number>\" (e.g. REQ-1, REQ-2) so tests and checks can refer to each requirement by its id. " +
                "After each answered question, update the BRS file on disk with what you have so far, so progress is never lost if the session ends. ",
            ExpectedArtifacts = ["devteam/features/<F>/specs.feature", "devteam/features/<F>/handoff.md"],
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.ScaffoldSpecs },
                new StepYaml { Builtin = BuiltinRegistry.CoreScaffold },
                new StepYaml { Builtin = BuiltinRegistry.RepoHygiene },
                new StepYaml { Builtin = BuiltinRegistry.CodeMap },
                new StepYaml { Builtin = BuiltinRegistry.ContextBundle },
                new StepYaml { Agent = new AgentYaml { Mode = "business-analyst" } },
                new StepYaml { Builtin = BuiltinRegistry.GherkinValidator },
                new StepYaml { Builtin = BuiltinRegistry.RenderHandoff },
            ],
        },
        ["developer"] = new RoleYaml
        {
            Signoff = Signoffs.PrCreated,
            UserInputRequired = false,
            WritesCode = true,
            SeedPrompt =
                " Reuse before writing: the workspace ships one shared core app. Read the context bundle's \"Shared core\" " +
                "section for its actual paths, and read devteam/features/<F>/manifest.yaml for this feature's own code " +
                "path(s) — the app's structure follows whatever the business-analyst declared there (which may not be a " +
                "backend/frontend split at all). Search the core for an existing type/component/service that already " +
                "does what a REQ needs, and extend it " +
                "in place when the behavior is shared. Only add feature-local code under your slice paths when the logic is " +
                "genuinely specific to this feature — never scaffold a second app or re-declare a core type under a new name. " +
                "The app already has its one project; never add a project or solution file inside a feature folder — a feature " +
                "adds source files to the existing project, nothing else. " +
                "Add tests first. Every test MUST name the requirement it proves by putting that requirement's id in the test " +
                "name (e.g. REQ_3_AddCommand_NegativeOperands_ShowsNegativeSum) — the coverage check finds each test by that id. " +
                "Also tag the code that implements a requirement with that id in a comment (e.g. // REQ-3) so completion can be measured. " +
                "If this is a desktop or UI app, make sure it actually starts — a broken startup crashes the app even when the tests pass. " +
                "Start the main window explicitly and avoid fragile relative resource URIs; set runCommand in the manifest if a custom launch command is needed. ",
            ExpectedArtifacts = ["devteam/features/<F>/code/"],
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.CodeMap },
                new StepYaml { Builtin = BuiltinRegistry.ContextBundle },
                new StepYaml
                {
                    Loop = new LoopYaml
                    {
                        Attempts = 3,
                        Steps =
                        [
                            new StepYaml { Agent = new AgentYaml { Mode = "developer" } },
                            // Cheap "does it even compile" check ahead of the much slower full
                            // test run — a scaffold-mismatch or a broken edit gets a clear,
                            // fast compiler error here instead of only surfacing (slower, less
                            // clearly) once verify_code's test run also fails for the same reason.
                            new StepYaml { Builtin = BuiltinRegistry.BuildCheck },
                            new StepYaml { Builtin = BuiltinRegistry.VerifyCode },
                        ],
                    },
                },
                new StepYaml { Builtin = BuiltinRegistry.CodeHygiene },
                new StepYaml { Builtin = BuiltinRegistry.AppLaunch },
                new StepYaml { Builtin = BuiltinRegistry.ReuseGate },
                new StepYaml { Builtin = BuiltinRegistry.ProjectStructure },
                new StepYaml { Builtin = BuiltinRegistry.SliceScope },
                new StepYaml { Builtin = BuiltinRegistry.RenderPr },
            ],
        },
        ["qa"] = new RoleYaml
        {
            Signoff = Signoffs.ReleaseApproval,
            UserInputRequired = false,
            WritesCode = true,
            ExpectedArtifacts = ["devteam/features/<F>/coverage.md"],
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.CodeMap },
                new StepYaml { Builtin = BuiltinRegistry.ContextBundle },
                new StepYaml { Agent = new AgentYaml { Mode = "qa" } },
                // QA verifies; it doesn't author tests. A failure here means the developer
                // stage didn't produce passing/sufficient tests — routed back to them (see
                // WorkflowEngine.RouteGateFailureToOwnerAsync) instead of QA retrying a loop
                // it structurally cannot make progress on.
                new StepYaml { Builtin = BuiltinRegistry.VerifyCode, ResponsibleRole = "developer" },
                new StepYaml { Builtin = BuiltinRegistry.CoverageMatrix, ResponsibleRole = "developer" },
                new StepYaml { Builtin = BuiltinRegistry.RenderHandoff },
            ],
        },
        // The last stage, and the only one with no agent and no human signoff: it just runs the
        // workspace's strict readiness checks. Because a feature only completes once the LAST
        // stage's gates pass, this is what makes "verified before it merges" true rather than
        // advisory. Kept deterministic (no Agent step) so the verdict never depends on an LLM.
        ["verification"] = new RoleYaml
        {
            Signoff = null,
            UserInputRequired = false,
            WritesCode = false,
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.FinalChecks },
            ],
        },
    };

    internal static List<ChallengeYaml> DefaultChallenges() =>
    [
        new ChallengeYaml
        {
            Producer = "business-analyst",
            Antagonist = new AgentYaml { Mode = "requirements-auditor" },
            Lint = BuiltinRegistry.GherkinValidator,
            Attempts = 2,
        },
        new ChallengeYaml
        {
            Producer = "developer",
            Antagonist = new AgentYaml { Mode = "qa" },
            Lint = BuiltinRegistry.ReuseGate,
            Attempts = 2,
        },
        new ChallengeYaml
        {
            Producer = "qa",
            Lint = BuiltinRegistry.CoverageMatrix,
            Attempts = 2,
        },
    ];
}

public static class Signoffs
{
    public const string ScopeFreeze = "scope-freeze";
    public const string RequirementsApproval = "requirements-approval";
    public const string PrCreated = "pr-created";
    public const string ReleaseApproval = "release-approval";
}

public sealed class ReleaseYaml
{
    public string Versioning { get; set; } = "semver";
}

public sealed class SlicesYaml
{
    public bool Scaffold { get; set; } = true;

    public List<string> Shared { get; set; } = ["Program.cs", "DevTeamDbContext.cs", "globals.css"];

    public string Artifacts { get; set; } = "devteam/features/<F>";

    public string CodeBack { get; set; } = "back-end/**/Features/<F>";

    public string CodeFront { get; set; } = "front-end/app/<F>";

    // The shared-core app each feature builds additively on — see CorePaths. Empty uses the
    // platform defaults (back-end/src/Core, front-end/app/core).
    public string CoreBack { get; set; } = CorePaths.DefaultBack;

    public string CoreFront { get; set; } = CorePaths.DefaultFront;
}

public sealed class RoleYaml
{
    public string? Signoff { get; set; }

    public List<StepYaml> Steps { get; set; } = [];

    public string? Builtin { get; set; }

    public AgentYaml? Agent { get; set; }

    public LoopYaml? Loop { get; set; }

    public bool UserInputRequired { get; set; }

    public bool WritesCode { get; set; }

    public string? SeedPrompt { get; set; }

    // Checks that must pass before this role's turn starts — see WorkflowRole.EntryGates.
    public List<StepYaml> EntryGates { get; set; } = [];

    public List<string> ExpectedArtifacts { get; set; } = [];

    // The single artifact this stage produces, if any — see WorkflowArtifact.
    public ArtifactYaml? Artifact { get; set; }
}

public sealed class ArtifactYaml
{
    // One of ArtifactRoots.All — validated by WorkflowDefinitionLoader.
    public string? Root { get; set; }

    public string? FileName { get; set; }

    // "text" | "json" — validated by WorkflowDefinitionLoader.
    public string Kind { get; set; } = "text";
}

public sealed class StepYaml
{
    public string? Builtin { get; set; }

    public AgentYaml? Agent { get; set; }

    public LoopYaml? Loop { get; set; }

    // A user-authored, LLM-graded check — see WorkflowStepKind.GatePrompt.
    public string? GatePrompt { get; set; }

    // A deterministic "has this specialist been consulted yet" check — see
    // WorkflowStepKind.RequiresSpecialist. Value is the SpecialistRole.Name required.
    public string? RequiresSpecialist { get; set; }

    // A deterministic "has this stage produced its declared Artifact yet" check — see
    // WorkflowStepKind.RequiresArtifact. Value is the name of the role whose Artifact is
    // required.
    public string? RequiresArtifact { get; set; }

    // See WorkflowStep.ResponsibleRole.
    public string? ResponsibleRole { get; set; }
}

public sealed class AgentYaml
{
    public string? Mode { get; set; }
}

public sealed class LoopYaml
{
    public int? Attempts { get; set; }

    public List<StepYaml> Steps { get; set; } = [];
}

public sealed class ChallengeYaml
{
    [YamlMember(Order = 1)]
    public string Producer { get; set; } = string.Empty;

    public AgentYaml? Antagonist { get; set; }

    public string? Lint { get; set; }

    public int? Attempts { get; set; }
}