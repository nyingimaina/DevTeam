using System.Text;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DevTeam.Broker.Workflow;

public sealed class WorkflowDefinitionLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    // Deliberately NOT DefaultValuesHandling.OmitDefaults: YamlDotNet compares against the
    // CLR type default (false for bool), not a property's own initializer default — several
    // properties here (e.g. Opinionated) default to true, so an explicit false would be
    // silently stripped as "same as unset" and revert to true on the next read.
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .Build();

    public WorkflowDefinition LoadDefault() => Convert(new WorkflowYaml(), challengesProvided: false);

    public WorkflowDefinition Load(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
            return LoadDefault();

        var rootKeys = GetRootKeys(yamlText);
        var parsed = _deserializer.Deserialize<WorkflowYaml>(yamlText) ?? new WorkflowYaml();
        var challengesProvided = rootKeys.Contains("challenges");

        if (!rootKeys.Contains("pipeline"))
            parsed.Pipeline = WorkflowYaml.DefaultPipeline();
        if (!challengesProvided)
            parsed.Challenges = WorkflowYaml.DefaultChallenges();

        return Convert(parsed, challengesProvided);
    }

    /// <summary>
    /// Parses raw YAML into the mutable WorkflowYaml object model (rather than the validated,
    /// immutable WorkflowDefinition Load() produces) — for callers that need to edit and
    /// re-serialize a pipeline (see PipelineEditorService), not just execute it. Applies the
    /// same "missing key keeps the default" semantics as Load().
    /// </summary>
    public WorkflowYaml ParseYamlObject(string? yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
            return new WorkflowYaml();

        var rootKeys = GetRootKeys(yamlText);
        var parsed = _deserializer.Deserialize<WorkflowYaml>(yamlText) ?? new WorkflowYaml();

        if (!rootKeys.Contains("pipeline"))
            parsed.Pipeline = WorkflowYaml.DefaultPipeline();
        if (!rootKeys.Contains("challenges"))
            parsed.Challenges = WorkflowYaml.DefaultChallenges();

        return parsed;
    }

    public static string SerializeYamlObject(WorkflowYaml yaml) => Serializer.Serialize(yaml);

    private static HashSet<string> GetRootKeys(string yamlText)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yamlText));
        var root = stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
        return root is null
            ? []
            : root.Children.Keys.OfType<YamlScalarNode>().Select(k => k.Value ?? string.Empty).ToHashSet();
    }

    private static WorkflowDefinition Convert(WorkflowYaml yaml, bool challengesProvided)
    {
        var errors = new List<string>();

        var pipeline = BuildPipeline(yaml, errors);
        var challenges = BuildChallenges(yaml, pipeline.Select(r => r.Name).ToHashSet(), challengesProvided, errors);
        ValidateArtifactReferences(pipeline, errors);
        ValidateArtifactUniqueness(pipeline, errors);

        if (errors.Count > 0)
            throw new WorkflowConfigurationException(
                "Invalid devteam/release.yaml:" + Environment.NewLine + string.Join(Environment.NewLine, errors.Select(e => "  - " + e)));

        return new WorkflowDefinition(
            new WorkflowRelease(yaml.Release.Versioning),
            new WorkflowSlices(
                yaml.Slices.Scaffold,
                yaml.Slices.Shared,
                yaml.Slices.Artifacts,
                yaml.Slices.CodeBack,
                yaml.Slices.CodeFront,
                yaml.Slices.CoreBack,
                yaml.Slices.CoreFront,
                yaml.Slices.CodePaths),
            pipeline,
            challenges,
            string.IsNullOrWhiteSpace(yaml.DocsRoot) ? "docs" : yaml.DocsRoot);
    }

    private static IReadOnlyList<WorkflowRole> BuildPipeline(WorkflowYaml yaml, List<string> errors)
    {
        if (yaml.Pipeline is null || yaml.Pipeline.Count == 0)
        {
            errors.Add("pipeline must define at least one role");
            return [];
        }

        if (yaml.Opinionated)
        {
            var required = new[] { "business-analyst", "developer", "qa" };
            var missing = required.Where(r => !yaml.Pipeline.ContainsKey(r)).ToArray();
            if (missing.Length > 0)
                errors.Add(
                    "opinionated pipeline must include the core roles: " + string.Join(", ", required) +
                    " (missing: " + string.Join(", ", missing) +
                    "). Set 'opinionated: false' to use a custom pipeline.");
        }

        var roles = new List<WorkflowRole>();
        foreach (var (name, role) in yaml.Pipeline)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add("pipeline contains a role with an empty name");
                continue;
            }

            var steps = ResolveRoleSteps(name, role, errors);
            var entryGates = role.EntryGates
                .Select((step, index) => ConvertStep(name, step, index, errors))
                .Where(step => step is not null)
                .Select(step => step!)
                .ToList();
            var artifact = ConvertArtifact(name, role.Artifact, errors);
            roles.Add(new WorkflowRole(
                name, steps, role.Signoff, role.UserInputRequired, role.ExpectedArtifacts,
                role.WritesCode, role.SeedPrompt, entryGates, artifact));
        }

        return roles;
    }

    private static WorkflowArtifact? ConvertArtifact(string roleName, ArtifactYaml? yaml, List<string> errors)
    {
        if (yaml is null)
            return null;

        if (string.IsNullOrWhiteSpace(yaml.Root) || !ArtifactRoots.IsKnown(yaml.Root))
            errors.Add(
                $"role '{roleName}' artifact 'root' must be one of: " + string.Join(", ", ArtifactRoots.All.OrderBy(x => x)));

        if (string.IsNullOrWhiteSpace(yaml.FileName))
            errors.Add($"role '{roleName}' artifact 'fileName' must be non-empty");

        var kind = yaml.Kind.Trim().ToLowerInvariant() switch
        {
            "json" => ArtifactKind.Json,
            "text" => ArtifactKind.Text,
            _ => (ArtifactKind?)null,
        };
        if (kind is null)
        {
            errors.Add($"role '{roleName}' artifact 'kind' must be 'text' or 'json' (got '{yaml.Kind}')");
            kind = ArtifactKind.Text;
        }

        return new WorkflowArtifact(yaml.Root ?? string.Empty, yaml.FileName ?? string.Empty, kind.Value);
    }

    // Cross-references a RequiresArtifact step's target stage exists and actually declares an
    // Artifact — checked after the whole pipeline is built (mirrors BuildChallenges' producer
    // validation), since it needs every role's Artifact to already be resolved.
    private static void ValidateArtifactReferences(IReadOnlyList<WorkflowRole> roles, List<string> errors)
    {
        var artifactByRole = roles.ToDictionary(r => r.Name, r => r.Artifact);
        foreach (var role in roles)
        {
            foreach (var step in FlattenSteps(role.Steps).Concat(FlattenSteps(role.EntryGates ?? [])))
            {
                if (step.Kind != WorkflowStepKind.RequiresArtifact)
                    continue;

                var target = step.RequiredArtifactStage!;
                if (!artifactByRole.TryGetValue(target, out var artifact))
                    errors.Add($"role '{role.Name}' references artifact stage '{target}', which is not a role in the pipeline");
                else if (artifact is null)
                    errors.Add($"role '{role.Name}' references artifact stage '{target}', which has no declared artifact");
            }
        }
    }

    // A stage's Artifact resolves to a flat <root>/<fileName> path (see
    // WorkflowEngine.ResolveStageArtifactPath) with no automatic per-stage subfolder, so two
    // roles declaring the same (Root, FileName) pair would silently collide on disk — reject
    // that at load time rather than let one role's artifact overwrite another's.
    private static void ValidateArtifactUniqueness(IReadOnlyList<WorkflowRole> roles, List<string> errors)
    {
        var byLocation = roles
            .Where(r => r.Artifact is not null)
            .GroupBy(r => (r.Artifact!.Root, r.Artifact.FileName));

        foreach (var group in byLocation)
        {
            if (group.Count() <= 1) continue;
            var names = string.Join(", ", group.Select(r => r.Name));
            errors.Add($"roles {names} all declare the same artifact location ('{group.Key.Root}/{group.Key.FileName}') — each stage's artifact must be unique");
        }
    }

    private static IEnumerable<WorkflowStep> FlattenSteps(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            if (step.Kind == WorkflowStepKind.Loop)
                foreach (var inner in FlattenSteps(step.LoopSteps ?? []))
                    yield return inner;
        }
    }

    private static IReadOnlyList<WorkflowStep> ResolveRoleSteps(string roleName, RoleYaml role, List<string> errors)
    {
        var shortcutSet = new[]
        {
            role.Builtin is not null,
            role.Agent is not null,
            role.Loop is not null,
        }.Count(v => v);

        if (role.Steps is { Count: > 0 })
        {
            if (shortcutSet > 0)
                errors.Add($"role '{roleName}' cannot combine 'steps' with a single-step shortcut ('builtin'/'agent'/'loop')");

            return role.Steps
                .Select((step, index) => ConvertStep(roleName, step, index, errors))
                .Where(step => step is not null)
                .Select(step => step!)
                .ToList();
        }

        if (shortcutSet == 0)
        {
            errors.Add($"role '{roleName}' must define at least one step (or a 'builtin', 'agent' or 'loop' shortcut)");
            return [];
        }

        if (shortcutSet > 1)
        {
            errors.Add($"role '{roleName}' must define exactly one of 'builtin', 'agent' or 'loop'");
            return [];
        }

        var shortcut = new StepYaml { Builtin = role.Builtin, Agent = role.Agent, Loop = role.Loop };
        var converted = ConvertStep(roleName, shortcut, 0, errors);
        return converted is null ? [] : [converted];
    }

    private static WorkflowStep? ConvertStep(string roleName, StepYaml step, int index, List<string> errors)
    {
        var set = new[]
        {
            step.Builtin is not null, step.Agent is not null, step.Loop is not null,
            step.GatePrompt is not null, step.RequiresSpecialist is not null, step.RequiresArtifact is not null,
        }.Count(v => v);

        if (set == 0)
        {
            errors.Add($"role '{roleName}' step #{index + 1} must define one of 'builtin', 'agent', 'loop', 'gatePrompt', 'requiresSpecialist' or 'requiresArtifact'");
            return null;
        }

        if (set > 1)
        {
            errors.Add($"role '{roleName}' step #{index + 1} must define exactly one of 'builtin', 'agent', 'loop', 'gatePrompt', 'requiresSpecialist' or 'requiresArtifact'");
            return null;
        }

        if (step.Builtin is not null)
        {
            if (!BuiltinRegistry.IsKnown(step.Builtin))
                errors.Add(
                    $"role '{roleName}' step #{index + 1} references unknown builtin '{step.Builtin}'. " +
                    "Known builtins: " + string.Join(", ", BuiltinRegistry.All.OrderBy(x => x)));

            return new WorkflowStep(WorkflowStepKind.Builtin, step.Builtin, null, null, null, ResponsibleRole: step.ResponsibleRole);
        }

        if (step.Agent is not null)
        {
            if (string.IsNullOrWhiteSpace(step.Agent.Mode))
                errors.Add($"role '{roleName}' step #{index + 1} agent must define a non-empty 'mode'");

            return new WorkflowStep(WorkflowStepKind.Agent, null, step.Agent.Mode, null, null, ResponsibleRole: step.ResponsibleRole);
        }

        if (step.GatePrompt is not null)
        {
            if (string.IsNullOrWhiteSpace(step.GatePrompt))
                errors.Add($"role '{roleName}' step #{index + 1} gatePrompt must be non-empty");

            return new WorkflowStep(WorkflowStepKind.GatePrompt, null, null, null, null, step.GatePrompt, step.ResponsibleRole);
        }

        if (step.RequiresSpecialist is not null)
        {
            if (string.IsNullOrWhiteSpace(step.RequiresSpecialist))
                errors.Add($"role '{roleName}' step #{index + 1} requiresSpecialist must be non-empty");

            return new WorkflowStep(
                WorkflowStepKind.RequiresSpecialist, null, null, null, null,
                ResponsibleRole: step.ResponsibleRole, RequiredSpecialist: step.RequiresSpecialist);
        }

        if (step.RequiresArtifact is not null)
        {
            if (string.IsNullOrWhiteSpace(step.RequiresArtifact))
                errors.Add($"role '{roleName}' step #{index + 1} requiresArtifact must be non-empty");

            return new WorkflowStep(
                WorkflowStepKind.RequiresArtifact, null, null, null, null,
                ResponsibleRole: step.ResponsibleRole, RequiredArtifactStage: step.RequiresArtifact);
        }

        var attempts = step.Loop!.Attempts ?? 3;
        if (attempts < 1)
            errors.Add($"role '{roleName}' step #{index + 1} loop 'attempts' must be at least 1");

        if (step.Loop.Steps is null || step.Loop.Steps.Count == 0)
        {
            errors.Add($"role '{roleName}' step #{index + 1} loop must contain at least one step");
            return new WorkflowStep(WorkflowStepKind.Loop, null, null, [], attempts);
        }

        var inner = step.Loop.Steps
            .Select((innerStep, innerIndex) => ConvertStep(roleName, innerStep, innerIndex, errors))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

        return new WorkflowStep(WorkflowStepKind.Loop, null, null, inner, attempts, ResponsibleRole: step.ResponsibleRole);
    }

    private static IReadOnlyList<WorkflowChallenge> BuildChallenges(
        WorkflowYaml yaml,
        HashSet<string> roleNames,
        bool challengesProvided,
        List<string> errors)
    {
        var challenges = new List<WorkflowChallenge>();
        if (yaml.Challenges is null)
            return challenges;

        foreach (var challenge in yaml.Challenges)
        {
            var producer = challenge.Producer?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(producer))
            {
                errors.Add("challenge is missing 'producer'");
                continue;
            }

            if (!roleNames.Contains(producer))
            {
                if (challengesProvided)
                    errors.Add($"challenge producer '{producer}' is not a role in the pipeline");
                continue;
            }

            var antagonistMode = challenge.Antagonist?.Mode;
            if (antagonistMode is not null && string.IsNullOrWhiteSpace(antagonistMode))
                errors.Add($"challenge for '{producer}' antagonist must define a 'mode'");

            if (challenge.Lint is not null && !BuiltinRegistry.IsKnown(challenge.Lint))
                errors.Add($"challenge for '{producer}' references unknown lint builtin '{challenge.Lint}'");

            var attempts = challenge.Attempts ?? 2;
            if (attempts < 1)
                errors.Add($"challenge for '{producer}' 'attempts' must be at least 1");

            challenges.Add(new WorkflowChallenge(producer, antagonistMode, challenge.Lint, attempts));
        }

        return challenges;
    }
}