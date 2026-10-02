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
                "has to clean up later. Once you know the real app type and hierarchy, if it doesn't fit a classic " +
                "backend/frontend split, write devteam/features/<F>/manifest.yaml yourself with your file tools (create " +
                "the directory if needed) — DevTeam only scaffolds a generic backend/frontend default there once you " +
                "finish, so writing it yourself is how a different hierarchy actually takes effect instead of a stray " +
                "back-end folder appearing regardless of what was decided here. Minimum shape: `feature: <F>`, " +
                "`title: <title>`, `codePaths: [\"src/Features/<F>\", \"src/Features/<F>.Tests\"]` (the folders this " +
                "feature's code and tests belong under), `corePathBack: \"src/Core\"` (where this app's one shared " +
                "project should live — leaving this unset defaults it to an unrelated back-end folder), " +
                "`testCommand: \"dotnet test <Solution>.slnx\"`. " +
                "Author the agreed requirements into devteam/features/<F>/" + ArtifactPaths.BrsFileName + " " +
                "(the BRS — Business Requirements Specification) using your file tools (create the directory if needed): " +
                "one \"## REQ-N: <Title>\" section per requirement, " +
                "each followed by a Given/When/Then acceptance-criteria sentence. Every requirement MUST contain Given, When and Then. " +
                "Use the exact id form \"REQ-<number>\" (e.g. REQ-1, REQ-2) so tests and checks can refer to each requirement by its id. " +
                "After each answered question, update the BRS file on disk with what you have so far, so progress is never lost if the session ends. ",
            ExpectedArtifacts = ["devteam/features/<F>/specs.feature", "devteam/features/<F>/handoff.md"],
            Steps =
            [
                // No context_bundle here: it fails outright with no manifest yet (which is the
                // normal state before the BA has even started), and it would be redundant even if
                // it degraded gracefully — the developer stage's own leading context_bundle call
                // regenerates context.md unconditionally once it starts, by which point
                // scaffold_specs below has already produced a real manifest.
                new StepYaml { Builtin = BuiltinRegistry.RepoHygiene },
                new StepYaml { Builtin = BuiltinRegistry.CodeMap },
                new StepYaml { Agent = new AgentYaml { Mode = "business-analyst" } },
                // Trailing, not leading: scaffold_specs/core_scaffold must see whatever real
                // hierarchy the BA settled on and (per the seed prompt above) wrote to
                // manifest.yaml itself — running them before the conversation locked every
                // feature into the generic backend/frontend default regardless of what was
                // actually decided (both gates are idempotent no-ops once a real manifest/project
                // already exists, so re-running them on every gate check is safe).
                new StepYaml { Builtin = BuiltinRegistry.ScaffoldSpecs },
                new StepYaml { Builtin = BuiltinRegistry.CoreScaffold },
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
                "Start the main window explicitly and avoid fragile relative resource URIs; set runCommand in the manifest if a custom launch command is needed. " +
                "You are no longer gated on the test suite passing: the full run happens in the test-runner stage, and the loop here only " +
                "verifies the files you changed (fast_lane). Iterate on real errors it reports - compile errors, type errors, and the tests " +
                "related to what you touched - and let the test-runner stage adjudicate anything the suite finds. " +
                "Do not edit, skip, delete or weaken a test to make a failure go away. If a test looks wrong or contradicts the BRS, say so " +
                "in your summary and leave the test alone - the test-runner stage files the challenge and the operator rules on it. ",
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
                            // Verify only what this feature actually changed: the incremental
                            // build for C#, a cached type check plus the tests related to the
                            // changed sources for TypeScript. The full suite is not this loop's
                            // job any more - it is the test-runner stage's, where a failure can
                            // be adjudicated against the BRS instead of retried blindly.
                            new StepYaml { Builtin = BuiltinRegistry.FastLane, ResponsibleRole = "developer" },
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
        // Between the developer and QA. The developer no longer runs the suite to gate itself, so
        // this is the one place a full run happens before QA: test_run produces the machine
        // facts, the test-runner agent turns them into a per-failure report, and test_report is
        // the deterministic verdict on that report. A failure routes back to the developer
        // (ResponsibleRole), which is where a code defect can actually be fixed — the test
        // runner itself writes no code. A disputed requirement is not the runner's to grant: it
        // becomes a challenge section, and the operator rules by messaging this stage (which
        // works for any live stage, interactive or not). The stage therefore does not demand
        // input up front — a green suite must never wait on a human.
        ["test-runner"] = new RoleYaml
        {
            Signoff = null,
            UserInputRequired = false,
            WritesCode = false,
            SeedPrompt =
                " You are the test runner for feature <F>. The full test suite has just run; the machine-extracted " +
                "result is in devteam/features/<F>/test-run.json (command, exit code, failed count, and the exact list of " +
                "failed test names) and the BRS with its REQ ids is next to it. " +
                "Write devteam/features/<F>/test-report.md from those facts - you are not guessing what failed, you are " +
                "explaining it. Read each failed test, and read the code it exercises, then write one section per failure: " +
                "\n\n## TEST-<n>: <the failed test's name exactly as the run recorded it>\n" +
                "- Test: <path to the test file>\n" +
                "- Requirement: <the REQ id the test name or body refers to>\n" +
                "- Verdict: fix | challenge\n" +
                "- Expected: <the acceptance criterion as the BRS states it>\n" +
                "- Observed: <what actually happened>\n" +
                "- Likely cause: <where in the code this comes from>\n" +
                "- Classification: introduced by this feature | pre-existing\n" +
                "\nEvery failed test the run recorded needs its own section - an unaccounted failure fails the stage. " +
                "Use \"Verdict: fix\" when the requirement is right and the code is wrong: that is the ordinary case, and the " +
                "developer is routed back to implement it. " +
                "Use \"Verdict: challenge\" only when the test itself is wrong, contradicts the BRS, or asserts something the " +
                "BRS never required. Then add: what the test asserts, why that is wrong, and the change you propose - as a " +
                "section line \"- Ruling: pending operator ruling\", and say in your reply that the operator needs to rule. " +
                "The operator rules by messaging this stage; when their ruling arrives, record it as \"- Ruling: accepted " +
                "(addendum BRS.addendum-<n>.md)\" or \"- Ruling: rejected - implement as written\". An accepted ruling means you " +
                "write the approved change as its own document " +
                "devteam/features/<F>/BRS.addendum-<n>.md (the affected REQ, the revised acceptance criterion as Given/When/Then) " +
                "and add one link line \"- See addendum: BRS.addendum-<n>.md\" at the end of the BRS. " +
                "Never rewrite the BRS or this report to make something pass - the BRS is the contract, and a changed " +
                "requirement is a new addendum plus a link, nothing else. Never edit, skip or delete a test yourself. ",
            ExpectedArtifacts = ["devteam/features/<F>/test-report.md"],
            Steps =
            [
                new StepYaml { Builtin = BuiltinRegistry.ContextBundle },
                new StepYaml { Builtin = BuiltinRegistry.TestRun },
                new StepYaml { Agent = new AgentYaml { Mode = "test-runner" } },
                new StepYaml { Builtin = BuiltinRegistry.TestReport, ResponsibleRole = "developer" },
            ],
        },
        ["qa"] = new RoleYaml
        {
            Signoff = Signoffs.ReleaseApproval,
            UserInputRequired = false,
            WritesCode = true,
            // QA had no written contract at all - only the generic autonomous preamble - so it was
            // free to improvise (and to fail the developer for things the developer cannot change).
            SeedPrompt =
                " You are the verifier, not an author. Read the BRS and map every REQ to the test that proves it and " +
                "the code that implements it, in devteam/features/<F>/coverage.md. Do not write or edit features or tests " +
                "to make the picture look complete: a missing test or an unimplemented REQ is a gap to record in " +
                "coverage.md (the coverage check routes it to the developer by itself - keep going). Finish with verdict " +
                "\"blocked\" only when a requirement is ambiguous or contradicts another so that nobody could implement or " +
                "test it, and with \"disputed\" when a check itself looks wrong; name the REQ id and say why. A person " +
                "then decides - the pipeline will not bounce it around. ",
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
                // Independent review of the developer's own committed work, not the developer's
                // own self-declared scope — slice_scope (developer's own gate) checks against a
                // frozen snapshot so the developer can't self-expand it, but only QA is positioned
                // to judge whether an out-of-slice touch was actually a legitimate, minimal
                // bugfix/refactor rather than unjustified scope creep. Same reasoning covers
                // architecture damage a slice check alone can't see: a file moved for no clear
                // reason, or a new file that duplicates logic already present elsewhere in the
                // diff/core instead of reusing it (a shortcut/workaround copy).
                new StepYaml
                {
                    GatePrompt =
                        "Review this feature's full branch diff (git diff release/<F>...feature/<F> --name-status) " +
                        "against the scope frozen at devteam/scope-snapshots/<F>.json when the developer stage began " +
                        "(its codePaths/core/shared fields) — devteam/features/<F>/manifest.yaml may have drifted from " +
                        "that since, so treat the snapshot as authoritative. For every changed file outside that frozen " +
                        "scope, judge whether it is a legitimate, minimal touch a real bugfix or refactor genuinely " +
                        "needed, or unjustified scope creep. Separately, look for architecture damage regardless of " +
                        "scope: files moved without a clear reason, or new files/functions that duplicate logic already " +
                        "in the shared core or elsewhere in this same diff instead of reusing it (a shortcut/workaround " +
                        "copy instead of a proper extension). If you find either problem, explain exactly which file(s) " +
                        "and why; if the diff is clean on both counts, say so plainly.",
                    ResponsibleRole = "developer",
                },
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

    public string CodeBack { get; set; } = CodePathDefaults.DefaultBack;

    public string CodeFront { get; set; } = CodePathDefaults.DefaultFront;

    // Free-form alternative to CodeBack/CodeFront above — for a release whose apps don't split
    // into backend/frontend at all. Only consulted as the pre-scaffold fallback (before a
    // feature's own manifest exists yet — see WorkflowEngine.ResolveRootDirectory); empty means
    // "use CodeBack/CodeFront", same precedence as SliceManifest.CodePaths/EffectiveCodePaths.
    public List<string> CodePaths { get; set; } = [];

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