using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class WorkflowDefinitionLoaderTests
{
    private static WorkflowDefinition LoadDefault() => new WorkflowDefinitionLoader().LoadDefault();

    private static WorkflowDefinition Load(string yaml) => new WorkflowDefinitionLoader().Load(yaml);

    [Fact]
    public void LoadDefault_DefinesSharedCoreSlicesAndGatesTheDeveloperOnReuseAndScope()
    {
        var definition = LoadDefault();

        Assert.Equal("back-end/src/Core", definition.Slices.CoreBack);
        Assert.Equal("front-end/app/core", definition.Slices.CoreFront);

        var developer = definition.Pipeline.Single(r => r.Name == "developer");
        Assert.Contains(developer.Steps, s => s.Builtin == "reuse_gate");
        Assert.Contains(developer.Steps, s => s.Builtin == "slice_scope");
        Assert.DoesNotContain(developer.Steps, s => s.Builtin == "slice_guard");
    }

    [Fact]
    public void Load_SlicesCorePathsDefaultAndCanBeOverridden()
    {
        Assert.Equal("back-end/src/Core", Load("release:\n  versioning: custom").Slices.CoreBack);

        var definition = Load("""
            slices:
              coreBack: back-end/Core
              coreFront: web/core
            """);

        Assert.Equal("back-end/Core", definition.Slices.CoreBack);
        Assert.Equal("web/core", definition.Slices.CoreFront);
    }

    [Fact]
    public void LoadDefault_SlicesCodePathsMatchTheSharedCodePathDefaultsConstant()
    {
        // Regression: CodeBack/CodeFront's default template used to be a literal string
        // duplicated independently in SlicesYaml and ScaffoldSpecsGate — this pins both sides to
        // the one shared constant so they can't drift apart again.
        var definition = LoadDefault();

        Assert.Equal(DevTeam.Broker.Gates.CodePathDefaults.DefaultBack, definition.Slices.CodeBack);
        Assert.Equal(DevTeam.Broker.Gates.CodePathDefaults.DefaultFront, definition.Slices.CodeFront);
    }

    [Fact]
    public void Load_SlicesCodePathsFreeFormListDefaultsEmptyAndCanBeOverridden()
    {
        // A release whose apps don't split into backend/frontend at all can declare its own
        // free-form default template list — same idea as SliceManifest.CodePaths, one level up.
        Assert.Empty(LoadDefault().Slices.EffectiveCodePaths);

        var definition = Load("""
            slices:
              codePaths:
                - src/Features/<F>
                - src/Features/<F>.Tests
            """);

        Assert.Equal(["src/Features/<F>", "src/Features/<F>.Tests"], definition.Slices.EffectiveCodePaths);
    }

    [Fact]
    public void LoadDefault_DefinesOpinionatedCorePipeline()
    {
        var definition = LoadDefault();

        Assert.Equal(
            ["business-analyst", "developer", "qa", "verification"],
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

        // The final stage is deterministic and automatic: no signoff, no agent — just the
        // strict readiness check that must pass before a feature can complete.
        var verification = definition.Pipeline[3];
        Assert.Null(verification.Signoff);
        Assert.False(verification.UserInputRequired);
        Assert.DoesNotContain(verification.Steps, s => s.Kind is WorkflowStepKind.Agent or WorkflowStepKind.Loop);
        Assert.Contains(verification.Steps, s => s.Builtin == "final_checks");
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
    public void Load_ParsesRequiresSpecialistStepsAsEntryOrExitGates()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              developer:
                entryGates:
                  - requiresSpecialist: business-analyst-reviewer
                steps:
                  - agent: { mode: developer }
                  - requiresSpecialist: database-admin
            """;
        var role = Load(yaml).Pipeline.Single();

        var entryGate = Assert.Single(role.EntryGates!);
        Assert.Equal(WorkflowStepKind.RequiresSpecialist, entryGate.Kind);
        Assert.Equal("business-analyst-reviewer", entryGate.RequiredSpecialist);

        var exitGate = role.Steps.Single(s => s.Kind == WorkflowStepKind.RequiresSpecialist);
        Assert.Equal("database-admin", exitGate.RequiredSpecialist);
    }

    [Fact]
    public void Load_RequiresSpecialistStepWithoutAName_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              developer:
                steps:
                  - requiresSpecialist: ""
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_ParsesADeclaredArtifactAndARequiresArtifactStep()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                  kind: json
                steps:
                  - agent: { mode: code-map }
                  - requiresArtifact: code-map
              business-analyst:
                entryGates:
                  - requiresArtifact: code-map
                agent: { mode: business-analyst }
            """;
        var definition = Load(yaml);
        var codeMap = definition.Pipeline.Single(r => r.Name == "code-map");

        Assert.NotNull(codeMap.Artifact);
        Assert.Equal("docs-root", codeMap.Artifact!.Root);
        Assert.Equal("codemap.json", codeMap.Artifact.FileName);
        Assert.Equal(ArtifactKind.Json, codeMap.Artifact.Kind);

        var exitGate = codeMap.Steps.Single(s => s.Kind == WorkflowStepKind.RequiresArtifact);
        Assert.Equal("code-map", exitGate.RequiredArtifactStage);

        var ba = definition.Pipeline.Single(r => r.Name == "business-analyst");
        var entryGate = Assert.Single(ba.EntryGates!);
        Assert.Equal(WorkflowStepKind.RequiresArtifact, entryGate.Kind);
        Assert.Equal("code-map", entryGate.RequiredArtifactStage);
    }

    [Fact]
    public void Load_ArtifactWithUnknownRoot_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: not-a-real-root
                  fileName: codemap.json
                agent: { mode: code-map }
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_ArtifactWithEmptyFileName_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: ""
                agent: { mode: code-map }
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_ArtifactWithUnknownKind_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                  kind: yaml
                agent: { mode: code-map }
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_RequiresArtifactStepWithoutAName_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              developer:
                steps:
                  - requiresArtifact: ""
            """;
        Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
    }

    [Fact]
    public void Load_RequiresArtifactReferencingAnUnknownStage_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              developer:
                steps:
                  - agent: { mode: developer }
                  - requiresArtifact: not-a-real-stage
            """;
        var exception = Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
        Assert.Contains("not-a-real-stage", exception.Message);
    }

    [Fact]
    public void Load_RequiresArtifactReferencingAStageWithNoDeclaredArtifact_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              code-map:
                agent: { mode: code-map }
              developer:
                steps:
                  - agent: { mode: developer }
                  - requiresArtifact: code-map
            """;
        var exception = Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
        Assert.Contains("no declared artifact", exception.Message);
    }

    [Fact]
    public void Load_TwoRolesDeclaringTheSameRootAndFileName_Throws()
    {
        const string yaml = """
            opinionated: false
            pipeline:
              code-map:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                agent: { mode: code-map }
              other-mapper:
                artifact:
                  root: docs-root
                  fileName: codemap.json
                agent: { mode: other-mapper }
            """;
        var exception = Assert.Throws<WorkflowConfigurationException>(() => Load(yaml));
        Assert.Contains("code-map", exception.Message);
        Assert.Contains("other-mapper", exception.Message);
    }

    [Fact]
    public void Load_DocsRootDefaultsToDocsAndCanBeOverridden()
    {
        Assert.Equal("docs", LoadDefault().DocsRoot);
        Assert.Equal("shared-docs", Load("docsRoot: shared-docs").DocsRoot);
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
        Assert.Equal("reuse_gate", dev.LintBuiltin);

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
        Assert.Equal(4, definition.Pipeline.Count);
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
        Assert.Equal(4, Load("").Pipeline.Count);
    }
}