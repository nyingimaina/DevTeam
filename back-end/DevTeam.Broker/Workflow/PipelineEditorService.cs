using DevTeam.Broker.Gates;

namespace DevTeam.Broker.Workflow;

/// <summary>
/// Reads and writes a workspace's devteam/release.yaml for the pipeline-authoring UI (Part
/// 2C) — reorder/add/remove stages, toggle WritesCode, and manage EntryGates/exit
/// GatePrompt steps, while every other step (Builtin/Loop/Agent wiring) is preserved
/// verbatim, never touched by this round trip. See PipelineEditorDtos.cs for why the edit
/// surface is scoped the way it is.
/// </summary>
public sealed class PipelineEditorService
{
    private readonly WorkflowDefinitionLoader _loader;

    public PipelineEditorService(WorkflowDefinitionLoader loader) => _loader = loader;

    public PipelineEditorDto Load(string workspacePath)
    {
        var yaml = ReadYaml(workspacePath);
        var roles = yaml.Pipeline.Select(kv => ToDto(kv.Key, kv.Value)).ToList();
        return new PipelineEditorDto(roles);
    }

    public void Save(string workspacePath, IReadOnlyList<PipelineEditorRoleDto> roles)
    {
        if (roles.Any(r => string.IsNullOrWhiteSpace(r.Name)))
            throw new WorkflowConfigurationException("Every stage needs a non-empty name.");
        var duplicate = roles.GroupBy(r => r.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new WorkflowConfigurationException($"Stage name '{duplicate.Key}' is used more than once.");

        var existing = ReadYaml(workspacePath);
        var newPipeline = new Dictionary<string, RoleYaml>();

        foreach (var roleDto in roles)
        {
            var role = existing.Pipeline.TryGetValue(roleDto.Name, out var existingRole)
                ? existingRole
                : new RoleYaml { Steps = [new StepYaml { Agent = new AgentYaml { Mode = roleDto.Name } }] };

            role.WritesCode = roleDto.WritesCode;
            role.Signoff = roleDto.Signoff;
            role.UserInputRequired = roleDto.UserInputRequired;
            role.EntryGates = roleDto.EntryGates.Select(ToStepYaml).ToList();

            // Preserve every existing step untouched except the top-level GatePrompt-kind
            // ones, which the editor owns completely — everything else (Builtin/Loop/Agent
            // wiring) is hand-authored and stays exactly as it was.
            var preservedSteps = role.Steps.Where(s => s.GatePrompt is null).ToList();
            preservedSteps.AddRange(roleDto.ExitGatePrompts.Select(ToStepYaml));
            role.Steps = preservedSteps;

            newPipeline[roleDto.Name] = role;
        }

        existing.Pipeline = newPipeline;
        // An edited pipeline can't guarantee it still contains the 3 core roles (the whole
        // point of Part 2 is letting it not), so it can no longer claim to be opinionated.
        existing.Opinionated = false;
        // Challenge editing isn't part of this editor's scope, but a challenge whose producer
        // no longer exists in the saved role set is an invalid file waiting to happen (the
        // engine rejects an unknown challenge producer once challenges are explicitly
        // present) — drop it rather than write out a pipeline that can't load.
        existing.Challenges = existing.Challenges
            .Where(c => c.Producer is not null && newPipeline.ContainsKey(c.Producer))
            .ToList();

        var yamlText = WorkflowDefinitionLoader.SerializeYamlObject(existing);
        // Never write a pipeline the engine itself would refuse to run — validate it through
        // the same loader RunStageAsync etc. use before it ever touches disk.
        _loader.Load(yamlText);

        var path = ArtifactPaths.ReleaseYamlPath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, yamlText);
    }

    private WorkflowYaml ReadYaml(string workspacePath)
    {
        var path = ArtifactPaths.ReleaseYamlPath(workspacePath);
        return _loader.ParseYamlObject(File.Exists(path) ? File.ReadAllText(path) : null);
    }

    private static PipelineEditorRoleDto ToDto(string name, RoleYaml role) => new(
        name,
        role.WritesCode,
        role.Signoff,
        role.UserInputRequired,
        FlattenStepNames(role.Steps),
        role.EntryGates.Where(s => s.Builtin is not null || s.GatePrompt is not null).Select(ToGateDto).ToList(),
        role.Steps.Where(s => s.GatePrompt is not null).Select(ToGateDto).ToList());

    private static GateStepEditorDto ToGateDto(StepYaml step) => step.GatePrompt is not null
        ? new GateStepEditorDto("gatePrompt", null, step.GatePrompt, step.ResponsibleRole)
        : new GateStepEditorDto("builtin", step.Builtin, null, step.ResponsibleRole);

    private static StepYaml ToStepYaml(GateStepEditorDto dto) => dto.Kind == "gatePrompt"
        ? new StepYaml { GatePrompt = dto.GatePromptText, ResponsibleRole = dto.ResponsibleRole }
        : new StepYaml { Builtin = dto.Builtin, ResponsibleRole = dto.ResponsibleRole };

    // Mirrors WorkflowEngine.FlattenStepNames but operates on the raw StepYaml model, since
    // the editor works with the pre-validation YAML object graph, not the resolved
    // WorkflowDefinition — kept as its own small copy rather than exposing WorkflowEngine's
    // internals to this unrelated read path.
    private static IReadOnlyList<string> FlattenStepNames(IReadOnlyList<StepYaml> steps)
    {
        var names = new List<string>();
        foreach (var step in steps)
        {
            if (step.Builtin is not null) names.Add(step.Builtin);
            else if (step.Agent is not null) names.Add($"agent:{step.Agent.Mode}");
            else if (step.GatePrompt is not null) names.Add("gate_prompt");
            else if (step.Loop is not null) names.AddRange(FlattenStepNames(step.Loop.Steps));
        }
        return names;
    }
}
