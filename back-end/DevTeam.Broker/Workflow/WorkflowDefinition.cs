namespace DevTeam.Broker.Workflow;

public enum WorkflowStepKind
{
    Builtin,
    Agent,
    Loop,
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
    string? SeedPrompt = null);

public sealed record WorkflowStep(
    WorkflowStepKind Kind,
    string? Builtin,
    string? AgentMode,
    IReadOnlyList<WorkflowStep>? LoopSteps,
    int? LoopAttempts);

public sealed record WorkflowChallenge(string Producer, string? AntagonistMode, string? LintBuiltin, int Attempts);

public sealed class WorkflowConfigurationException : Exception
{
    public WorkflowConfigurationException(string message) : base(message)
    {
    }
}