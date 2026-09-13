using YamlDotNet.Serialization;

namespace DevTeam.Broker.Workflow;

public sealed class WorkflowYaml
{
    public bool Opinionated { get; set; } = true;

    public ReleaseYaml Release { get; set; } = new();

    public SlicesYaml Slices { get; set; } = new();

    public Dictionary<string, RoleYaml> Pipeline { get; set; } = DefaultPipeline();

    public List<ChallengeYaml> Challenges { get; set; } = DefaultChallenges();

    internal static Dictionary<string, RoleYaml> DefaultPipeline() => new()
    {
        ["business-analyst"] = new RoleYaml
        {
            Signoff = Signoffs.RequirementsApproval,
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
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.ContextBundle },
                new StepYaml { Agent = new AgentYaml { Mode = "qa" } },
                new StepYaml { Builtin = BuiltinRegistry.VerifyCode },
                new StepYaml { Builtin = BuiltinRegistry.CoverageMatrix },
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
}

public sealed class StepYaml
{
    public string? Builtin { get; set; }

    public AgentYaml? Agent { get; set; }

    public LoopYaml? Loop { get; set; }
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