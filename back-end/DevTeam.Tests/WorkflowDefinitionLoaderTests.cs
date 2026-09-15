using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class WorkflowDefinitionLoaderTests
{
    private static WorkflowDefinition LoadDefault() => new WorkflowDefinitionLoader().LoadDefault();

    private static WorkflowDefinition Load(string yaml) => new WorkflowDefinitionLoader().Load(yaml);

    [Fact]
    public void LoadDefault_DefinesOpinionatedCorePipeline()
    {
        var definition = LoadDefault();

        Assert.Equal(
            ["business-analyst", "developer", "qa"],
            definition.Pipeline.Select(r => r.Name).ToArray());

        var ba = definition.Pipeline[0];
        Assert.Equal("requirements-approval", ba.Signoff);
        Assert.Equal(WorkflowStepKind.Builtin, ba.Steps[0].Kind);
        Assert.Contains(ba.Steps, s => s.Kind == WorkflowStepKind.Agent && s.AgentMode == "business-analyst");

        var dev = definition.Pipeline[1];
        Assert.Equal("pr-created", dev.Signoff);
        Assert.Contains(dev.Steps, s => s.Kind == WorkflowStepKind.Loop && s.LoopSteps!.Any(x => x.AgentMode == "developer"));

        var qa = definition.Pipeline[2];
        Assert.Equal("release-approval", qa.Signoff);
        Assert.Equal("coverage_matrix", qa.Steps.Single(s => s.Kind == WorkflowStepKind.Builtin && s.Builtin == "coverage_matrix").Builtin);
    }

    [Fact]
    public void LoadDefault_QaVerifyAndCoverageGatesAreOwnedByDeveloper()
    {
        // QA verifies; it can't author tests, so a failure here must be routed back to
        // developer instead of QA retrying a loop it structurally cannot fix.
        var qa = LoadDefault().Pipeline.Single(r => r.Name == "qa");

        Assert.Equal("developer", qa.Steps.Single(s => s.Builtin == "verify_code").ResponsibleRole);
        Assert.Equal("developer", qa.Steps.Single(s => s.Builtin == "coverage_matrix").ResponsibleRole);
        // A gate with no ResponsibleRole override defaults to the owning role (unset here).
        Assert.Null(qa.Steps.Single(s => s.Builtin == "render_handoff").ResponsibleRole);
    }

    [Fact]
    public void Load_ParsesGatePromptStepsAndEntryGates()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              developer:
                entryGates:
                  - gatePrompt: "Confirm the BRS mentions accessibility requirements."
                    responsibleRole: business-analyst
                steps:
                  - agent: { mode: developer }
                  - gatePrompt: "Review the diff for security issues."
            """;
        var role = Load(yaml).Pipeline.Single();

        var entryGate = Assert.Single(role.EntryGates!);
        Assert.Equal(WorkflowStepKind.GatePrompt, entryGate.Kind);
        Assert.Equal("Confirm the BRS mentions accessibility requirements.", entryGate.GatePromptText);
        Assert.Equal("business-analyst", entryGate.ResponsibleRole);

        var exitGate = role.Steps.Single(s => s.Kind == WorkflowStepKind.GatePrompt);
        Assert.Equal("Review the diff for security issues.", exitGate.GatePromptText);
        Assert.Null(exitGate.ResponsibleRole);
    }

    [Fact]
    public void Load_GatePromptStepWithoutText_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              developer:
                steps:
                  - gatePrompt: ""
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void LoadDefault_DefinesChallengePairs()
    {
        var definition = LoadDefault();

        var ba = definition.Challenges.Single(c => c.Producer == "business-analyst");
        Assert.Equal("requirements-auditor", ba.AntagonistMode);
        Assert.Equal("gherkin_validator", ba.LintBuiltin);

        var dev = definition.Challenges.Single(c => c.Producer == "developer");
        Assert.Equal("qa", dev.AntagonistMode);

        var qa = definition.Challenges.Single(c => c.Producer == "qa");
        Assert.Null(qa.AntagonistMode);
        Assert.Equal("coverage_matrix", qa.LintBuiltin);
    }

    [Fact]
    public void Load_PartialYaml_KeepsDefaults()
    {
        var definition = Load("release:\n  versioning: custom");

        Assert.Equal("custom", definition.Release.Versioning);
        Assert.Equal("semver", new WorkflowDefinitionLoader().LoadDefault().Release.Versioning);
        Assert.Equal(3, definition.Pipeline.Count);
    }

    [Fact]
    public void Load_AddsRoleAndOverridesAttempts()
    {
        const string yaml = """
            pipeline:
              business-analyst:
                agent: { mode: business-analyst }
              developer:
                agent: { mode: developer }
              qa:
                agent: { mode: qa }
              documenter:
                agent: { mode: documenter }
            """;
        var definition = Load(yaml);

        Assert.Equal(4, definition.Pipeline.Count);
        Assert.Equal("documenter", definition.Pipeline[^1].Name);
        Assert.Equal(WorkflowStepKind.Agent, definition.Pipeline[^1].Steps[0].Kind);
    }

    [Fact]
    public void Load_ParsesStepsWithLoopAndDefaults()
    {
        const string yaml = """
            pipeline:
              business-analyst:
                signoff: plan-approved
                steps:
                  - builtin: scaffold_specs
                  - loop: { attempts: 4, steps: [ agent: { mode: business-analyst }, builtin: gherkin_validator ] }
                  - agent: { mode: requirements-auditor }
            opinionated: false
            """;
        var role = Load(yaml).Pipeline.Single();

        Assert.Equal("business-analyst", role.Name);
        Assert.Equal("plan-approved", role.Signoff);
        Assert.Equal(3, role.Steps.Count);
        Assert.Equal(WorkflowStepKind.Loop, role.Steps[1].Kind);
        Assert.Equal(4, role.Steps[1].LoopAttempts);
        Assert.Equal("requirements-auditor", role.Steps[2].AgentMode);
    }

    [Fact]
    public void Load_MissingCoreRole_Throws()
    {
        const string yaml = """
            pipeline:
              developer:
                agent: { mode: developer }
            """;
        var exception = Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
        Assert.Contains("business-analyst", exception.Message);
    }

    [Fact]
    public void Load_RelaxedAllowsCustomPipeline()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              researcher:
                agent: { mode: researcher }
            """;
        var definition = Load(yaml);
        Assert.Equal(["researcher"], definition.Pipeline.Select(r => r.Name).ToArray());
    }

    [Fact]
    public void Load_ParsesWritesCodeOnACustomRoleNotNamedDeveloperOrQa()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              researcher:
                writesCode: true
                agent: { mode: researcher }
              archivist:
                agent: { mode: archivist }
            """;
        var definition = Load(yaml);

        Assert.True(definition.Pipeline.Single(r => r.Name == "researcher").WritesCode);
        Assert.False(definition.Pipeline.Single(r => r.Name == "archivist").WritesCode);
    }

    [Fact]
    public void Load_UnknownBuiltin_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              business-analyst:
                steps:
                  - builtin: does_not_exist
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_AgentStepWithoutMode_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              business-analyst:
                steps:
                  - agent: { }
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_ChallengeWithUnknownProducer_Throws()
    {
        const string yaml = """
            pipeline:
              business-analyst:
                agent: { mode: business-analyst }
              developer:
                agent: { mode: developer }
              qa:
                agent: { mode: qa }
            challenges:
              - producer: not-a-role
                antagonist: { mode: qa }
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_EmptyPipeline_Throws()
    {
        Assert.Throws<WorkflowConfigurationException>(() => Load("pipeline: { }"));
    }

    [Fact]
    public void Load_EmptyOrWhitespace_FallsBackToDefaults()
    {
        Assert.Equal(3, Load("").Pipeline.Count);
    }
}