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
                " Author the agreed requirements into devteam/features/<F>/" + ArtifactPaths.BrsFileName + " " +
                "(the BRS — Business Requirements Specification) using your file tools (create the directory if needed): " +
                "one \"## REQ-N: <Title>\" section per requirement, " +
                "each followed by a Given/When/Then acceptance-criteria sentence. Every requirement MUST contain Given, When and Then. ",
            ExpectedArtifacts = ["devteam/features/<F>/specs.feature", "devteam/features/<F>/handoff.md"],
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.ScaffoldSpecs },
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
            ExpectedArtifacts = ["devteam/features/<F>/code/"],
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.ContextBundle },
                new StepYaml
                {
                    Loop = new LoopYaml
                    {
                        Attempts = 3,
                        Steps =
                        [
                            new StepYaml { Agent = new AgentYaml { Mode = "developer" } },
                            new StepYaml { Builtin = BuiltinRegistry.VerifyCode },
                        ],
                    },
                },
                new StepYaml { Builtin = BuiltinRegistry.CodeHygiene },
                new StepYaml { Builtin = BuiltinRegistry.SliceGuard },
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