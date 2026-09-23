using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class PipelineEditorServiceTests
{
    private static PipelineEditorService CreateService() => new(new WorkflowDefinitionLoader());

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "devteam-pipeline-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Load_WithNoReleaseYaml_ReturnsTheDefaultPipelineInOrder()
    {
        var workspace = CreateWorkspace();
        try
        {
            var editor = CreateService().Load(workspace);

            Assert.Equal(["business-analyst", "developer", "qa", "verification"], editor.Roles.Select(r => r.Name).ToArray());
            var dev = editor.Roles.Single(r => r.Name == "developer");
            Assert.True(dev.WritesCode);
            Assert.Contains("agent:developer", dev.StepSummary);
            Assert.Contains("code_hygiene", dev.StepSummary);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void DefaultPrompts_RequireRequirementIdsSoCoverageIsUnambiguous()
    {
        var workspace = CreateWorkspace();
        try
        {
            var editor = CreateService().Load(workspace);

            var ba = editor.Roles.Single(r => r.Name == "business-analyst");
            var developer = editor.Roles.Single(r => r.Name == "developer");

            Assert.Contains("REQ-", ba.SeedPrompt);
            Assert.Contains("REQ", developer.SeedPrompt);
            Assert.Contains("test name", developer.SeedPrompt, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAReorderedAddedAndRemovedPipeline()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var original = service.Load(workspace);

            // Reorder (qa first), drop business-analyst, add a brand-new "researcher" role.
            var qa = original.Roles.Single(r => r.Name == "qa");
            var developer = original.Roles.Single(r => r.Name == "developer");
            var researcher = new PipelineEditorRoleDto(
                "researcher", WritesCode: false, Signoff: null, UserInputRequired: false,
                StepSummary: [], EntryGates: [], ExitGatePrompts: []);

            service.Save(workspace, [qa, developer, researcher]);
            var updated = service.Load(workspace);

            Assert.Equal(["qa", "developer", "researcher"], updated.Roles.Select(r => r.Name).ToArray());
            Assert.Contains("agent:researcher", updated.Roles.Single(r => r.Name == "researcher").StepSummary);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_PreservesAnExistingRolesBuiltinAndLoopStepsVerbatim()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var original = service.Load(workspace);
            var developer = original.Roles.Single(r => r.Name == "developer");

            // Edit only WritesCode; the role's complex Loop/Builtin wiring is untouched.
            var edited = developer with { WritesCode = true };
            service.Save(workspace, [original.Roles.Single(r => r.Name == "business-analyst"), edited, original.Roles.Single(r => r.Name == "qa")]);

            var updated = service.Load(workspace).Roles.Single(r => r.Name == "developer");
            Assert.Equal(
                ["code_map", "context_bundle", "agent:developer", "build_check", "verify_code", "code_hygiene", "app_launch", "reuse_gate", "project_structure", "slice_scope", "render_pr"],
                updated.StepSummary);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_ReplacesOnlyTheTopLevelGatePromptStepsOnAnExistingRole()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var original = service.Load(workspace);
            var developer = original.Roles.Single(r => r.Name == "developer");

            var withGatePrompt = developer with
            {
                ExitGatePrompts = [new GateStepEditorDto("gatePrompt", null, "Review the diff for security issues.", "developer")],
            };
            service.Save(workspace, [original.Roles.Single(r => r.Name == "business-analyst"), withGatePrompt, original.Roles.Single(r => r.Name == "qa")]);

            var updated = service.Load(workspace).Roles.Single(r => r.Name == "developer");
            Assert.Single(updated.ExitGatePrompts);
            Assert.Equal("Review the diff for security issues.", updated.ExitGatePrompts[0].GatePromptText);
            // The role's existing builtin/loop steps must still be there alongside it.
            Assert.Contains("code_hygiene", updated.StepSummary);
            Assert.Contains("gate_prompt", updated.StepSummary);

            // Saving again with a different gate-prompt text replaces, not accumulates.
            var replaced = updated with
            {
                ExitGatePrompts = [new GateStepEditorDto("gatePrompt", null, "Check for accessibility issues.", null)],
            };
            service.Save(workspace, [original.Roles.Single(r => r.Name == "business-analyst"), replaced, original.Roles.Single(r => r.Name == "qa")]);
            var replacedResult = service.Load(workspace).Roles.Single(r => r.Name == "developer");
            Assert.Single(replacedResult.ExitGatePrompts);
            Assert.Equal("Check for accessibility issues.", replacedResult.ExitGatePrompts[0].GatePromptText);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_RoundTripsEntryGatesWithResponsibleRole()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var original = service.Load(workspace);
            var qa = original.Roles.Single(r => r.Name == "qa") with
            {
                EntryGates = [new GateStepEditorDto("gatePrompt", null, "Confirm the PR is ready for review.", "developer")],
            };
            service.Save(workspace, [original.Roles.Single(r => r.Name == "business-analyst"), original.Roles.Single(r => r.Name == "developer"), qa]);

            var updated = service.Load(workspace).Roles.Single(r => r.Name == "qa");
            var entryGate = Assert.Single(updated.EntryGates);
            Assert.Equal("gatePrompt", entryGate.Kind);
            Assert.Equal("Confirm the PR is ready for review.", entryGate.GatePromptText);
            Assert.Equal("developer", entryGate.ResponsibleRole);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_RoundTripsRequiresSpecialistAsEntryOrExitGates()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var original = service.Load(workspace);
            var developer = original.Roles.Single(r => r.Name == "developer") with
            {
                EntryGates = [new GateStepEditorDto("requiresSpecialist", null, null, null) { RequiredSpecialist = "business-analyst-reviewer" }],
                ExitGatePrompts = [new GateStepEditorDto("requiresSpecialist", null, null, "developer") { RequiredSpecialist = "database-admin" }],
            };
            service.Save(workspace, [original.Roles.Single(r => r.Name == "business-analyst"), developer, original.Roles.Single(r => r.Name == "qa")]);

            var updated = service.Load(workspace).Roles.Single(r => r.Name == "developer");

            var entryGate = Assert.Single(updated.EntryGates);
            Assert.Equal("requiresSpecialist", entryGate.Kind);
            Assert.Equal("business-analyst-reviewer", entryGate.RequiredSpecialist);

            var exitGate = Assert.Single(updated.ExitGatePrompts);
            Assert.Equal("requiresSpecialist", exitGate.Kind);
            Assert.Equal("database-admin", exitGate.RequiredSpecialist);
            Assert.Equal("developer", exitGate.ResponsibleRole);
            Assert.Contains("requires_specialist:database-admin", updated.StepSummary);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Load_ExposesTheDefaultBusinessAnalystSeedPrompt()
    {
        var workspace = CreateWorkspace();
        try
        {
            var ba = CreateService().Load(workspace).Roles.Single(r => r.Name == "business-analyst");
            Assert.Contains("BRS", ba.SeedPrompt);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_RoundTripsSeedPromptOnACustomRole()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var codeMap = new PipelineEditorRoleDto(
                "code-map", WritesCode: false, Signoff: null, UserInputRequired: false,
                StepSummary: [], EntryGates: [], ExitGatePrompts: [],
                SeedPrompt: "Scan the codebase and emit its module graph.");
            service.Save(workspace, [codeMap]);

            var updated = service.Load(workspace).Roles.Single(r => r.Name == "code-map");
            Assert.Equal("Scan the codebase and emit its module graph.", updated.SeedPrompt);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_RoundTripsADeclaredArtifactAndARequiresArtifactExitGate()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var original = service.Load(workspace);
            var codeMap = new PipelineEditorRoleDto(
                "code-map", WritesCode: false, Signoff: null, UserInputRequired: false,
                StepSummary: [], EntryGates: [], ExitGatePrompts: [],
                Artifact: new ArtifactEditorDto("docs-root", "codemap.json", "json"));
            var businessAnalyst = original.Roles.Single(r => r.Name == "business-analyst") with
            {
                EntryGates = [new GateStepEditorDto("requiresArtifact", null, null, null, RequiredArtifactStage: "code-map")],
            };
            service.Save(workspace, [codeMap, businessAnalyst, original.Roles.Single(r => r.Name == "developer"), original.Roles.Single(r => r.Name == "qa")]);

            var updated = service.Load(workspace);
            var updatedCodeMap = updated.Roles.Single(r => r.Name == "code-map");
            Assert.NotNull(updatedCodeMap.Artifact);
            Assert.Equal("docs-root", updatedCodeMap.Artifact!.Root);
            Assert.Equal("codemap.json", updatedCodeMap.Artifact.FileName);
            Assert.Equal("json", updatedCodeMap.Artifact.Kind);

            var updatedBa = updated.Roles.Single(r => r.Name == "business-analyst");
            var entryGate = Assert.Single(updatedBa.EntryGates);
            Assert.Equal("requiresArtifact", entryGate.Kind);
            Assert.Equal("code-map", entryGate.RequiredArtifactStage);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_WithADuplicateRoleName_ThrowsInsteadOfSilentlyOverwriting()
    {
        var workspace = CreateWorkspace();
        try
        {
            var role = new PipelineEditorRoleDto("researcher", false, null, false, [], [], []);
            Assert.Throws<WorkflowConfigurationException>(() => CreateService().Save(workspace, [role, role with { WritesCode = true }]));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void Save_SetsOpinionatedFalse_SoTheEngineAcceptsAPipelineMissingACoreRole()
    {
        var workspace = CreateWorkspace();
        try
        {
            var service = CreateService();
            var researcher = new PipelineEditorRoleDto(
                "researcher", WritesCode: false, Signoff: null, UserInputRequired: false,
                StepSummary: [], EntryGates: [], ExitGatePrompts: []);
            service.Save(workspace, [researcher]);

            // If Save had left opinionated:true (the WorkflowYaml default), loading this
            // through the real engine loader would throw for missing business-analyst/
            // developer/qa — this proves it didn't.
            var definition = new WorkflowDefinitionLoader().Load(
                File.ReadAllText(Path.Combine(workspace, "devteam", "release.yaml")));
            Assert.Equal(["researcher"], definition.Pipeline.Select(r => r.Name).ToArray());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
