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
}

public sealed record WorkflowDefinition(
    WorkflowRelease Release,
    WorkflowSlices Slices,
    IReadOnlyList<WorkflowRole> Pipeline,
    IReadOnlyList<WorkflowChallenge> Challenges);

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
    IReadOnlyList<WorkflowStep>? EntryGates = null);

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
    string? ResponsibleRole = null);

public sealed record WorkflowChallenge(string Producer, string? AntagonistMode, string? LintBuiltin, int Attempts);

public sealed class WorkflowConfigurationException : Exception
{
    public WorkflowConfigurationException(string message) : base(message)
    {
    }
}