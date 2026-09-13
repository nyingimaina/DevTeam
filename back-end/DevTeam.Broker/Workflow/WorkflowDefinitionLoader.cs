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
                yaml.Slices.CodeFront),
            pipeline,
            challenges);
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
            roles.Add(new WorkflowRole(name, steps, role.Signoff));
        }

        return roles;
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
        var set = new[] { step.Builtin is not null, step.Agent is not null, step.Loop is not null }
            .Count(v => v);

        if (set == 0)
        {
            errors.Add($"role '{roleName}' step #{index + 1} must define one of 'builtin', 'agent' or 'loop'");
            return null;
        }

        if (set > 1)
        {
            errors.Add($"role '{roleName}' step #{index + 1} must define exactly one of 'builtin', 'agent' or 'loop'");
            return null;
        }

        if (step.Builtin is not null)
        {
            if (!BuiltinRegistry.IsKnown(step.Builtin))
                errors.Add(
                    $"role '{roleName}' step #{index + 1} references unknown builtin '{step.Builtin}'. " +
                    "Known builtins: " + string.Join(", ", BuiltinRegistry.All.OrderBy(x => x)));

            return new WorkflowStep(WorkflowStepKind.Builtin, step.Builtin, null, null, null);
        }

        if (step.Agent is not null)
        {
            if (string.IsNullOrWhiteSpace(step.Agent.Mode))
                errors.Add($"role '{roleName}' step #{index + 1} agent must define a non-empty 'mode'");

            return new WorkflowStep(WorkflowStepKind.Agent, null, step.Agent.Mode, null, null);
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

        return new WorkflowStep(WorkflowStepKind.Loop, null, null, inner, attempts);
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