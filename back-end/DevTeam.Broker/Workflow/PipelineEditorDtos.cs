namespace DevTeam.Broker.Workflow;

// The pipeline-authoring UI (Part 2C) edits at the role level — order, name, WritesCode,
// signoff, EntryGates, and the top-level GatePrompt-kind exit steps — but deliberately does
// NOT let a human hand-edit a role's Builtin/Loop/Agent step wiring from a form; that stays
// YAML-only, hand-authored. GateStepEditorDto/PipelineEditorRoleDto exist purely to carry
// that editable slice back and forth; StepSummary is a read-only projection of the full step
// list (reusing WorkflowEngine's own flattening) so the editor can show what a role actually
// does without offering to change it.

public sealed record GateStepEditorDto(
    string Kind, // "builtin" | "gatePrompt"
    string? Builtin,
    string? GatePromptText,
    string? ResponsibleRole);

public sealed record PipelineEditorRoleDto(
    string Name,
    bool WritesCode,
    string? Signoff,
    bool UserInputRequired,
    IReadOnlyList<string> StepSummary,
    IReadOnlyList<GateStepEditorDto> EntryGates,
    IReadOnlyList<GateStepEditorDto> ExitGatePrompts);

public sealed record PipelineEditorDto(IReadOnlyList<PipelineEditorRoleDto> Roles);

public sealed record SavePipelineEditorRequest(string WorkspacePath, IReadOnlyList<PipelineEditorRoleDto> Roles);
