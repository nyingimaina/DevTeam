namespace DevTeam.Broker.Workflow;

public enum WorkflowStepKind
{
    Builtin,
    Agent,
    Loop,
    // An LLM-graded check: a user-authored natural-language prompt sent to a fresh session,
    // passed when the response ends in "end_turn" — same mechanism as the antagonist Challenge,
    // generalized to run at any position (entry or exit) on any role, not just one fixed
    // post-hoc slot per producer.
    GatePrompt,
    // A deterministic check (not LLM-graded, unlike GatePrompt): passes only if a
    // SpecialistConsultation already exists for this stage run and the named specialist —
    // makes delegation (Part 3) enforceable instead of purely advisory, e.g. "this stage
    // can't finish until database-admin has been consulted."
    RequiresSpecialist,
    // A deterministic check (Part 4): passes only if the named stage's own declared Artifact
    // exists on disk (and, if it declares ArtifactKind.Json, parses as valid JSON). No LLM
    // call — a cheap, reliable alternative to a GatePrompt for "has stage X produced its
    // output" questions.
    RequiresArtifact,
}

public enum ArtifactKind
{
    Text,
    Json,
}

// A stage may declare at most one of these — see WorkflowRole.Artifact. Its real path is
// always <resolved Root>/<owning stage's own name>/<FileName>, so the owning stage's
// (already-unique) name doubles as the artifact's key: no separate artifact-naming system is
// needed, and two stages can never collide on output location even by accident.
public sealed record WorkflowArtifact(string Root, string FileName, ArtifactKind Kind);

public sealed record WorkflowDefinition(
    WorkflowRelease Release,
    WorkflowSlices Slices,
    IReadOnlyList<WorkflowRole> Pipeline,
    IReadOnlyList<WorkflowChallenge> Challenges,
    // A workspace-wide (not per-feature) folder for cross-cutting docs artifacts — see
    // ArtifactRoots.DocsRoot.
    string DocsRoot = "docs");

public sealed record WorkflowRelease(string Versioning);

public sealed record WorkflowSlices(
    bool Scaffold,
    IReadOnlyList<string> Shared,
    string Artifacts,
    string CodeBack,
    string CodeFront);

public sealed record WorkflowRole(
    string Name,
    IReadOnlyList<WorkflowStep> Steps,
    string? Signoff,
    bool UserInputRequired,
    IReadOnlyList<string> ExpectedArtifacts,
    bool WritesCode = false,
    // Text spliced into this role's opening prompt (with "<F>" replaced by the feature key,
    // same convention as ExpectedArtifacts) — for role-specific instructions that belong in
    // the role's own definition rather than a stage-name check in engine code.
    string? SeedPrompt = null,
    // Checks that must pass before this role's own turn starts — symmetric to the existing
    // exit-side Steps, which must pass before the role can finish. Same WorkflowStep shape,
    // so an entry gate can be a builtin or a GatePrompt just like an exit one.
    IReadOnlyList<WorkflowStep>? EntryGates = null,
    // The single artifact this stage is expected to produce, if any — see WorkflowArtifact.
    // Referenced by other stages' RequiresArtifact gates and by the <stage-name/artifact.file>
    // placeholder in prompt text.
    WorkflowArtifact? Artifact = null);

public sealed record WorkflowStep(
    WorkflowStepKind Kind,
    string? Builtin,
    string? AgentMode,
    IReadOnlyList<WorkflowStep>? LoopSteps,
    int? LoopAttempts,
    // Only used when Kind == GatePrompt.
    string? GatePromptText = null,
    // The role that should act when THIS gate fails — defaults to the owning role's own name
    // (today's implicit behavior) when unset. Set to a different role when the gate checks
    // something only that other role can actually fix (e.g. QA's verify_code failing because
    // the developer didn't write tests — QA cannot author tests itself).
    string? ResponsibleRole = null,
    // Only used when Kind == RequiresSpecialist — the SpecialistRole.Name that must have a
    // recorded SpecialistConsultation for this stage run.
    string? RequiredSpecialist = null,
    // Only used when Kind == RequiresArtifact — the name of the role whose declared Artifact
    // must exist (and, if it declares ArtifactKind.Json, parse) for this gate to pass.
    string? RequiredArtifactStage = null);

public sealed record WorkflowChallenge(string Producer, string? AntagonistMode, string? LintBuiltin, int Attempts);

public sealed class WorkflowConfigurationException : Exception
{
    public WorkflowConfigurationException(string message) : base(message)
    {
    }
}