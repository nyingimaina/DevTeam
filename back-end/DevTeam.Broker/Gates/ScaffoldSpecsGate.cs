using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class ScaffoldSpecsGate : IGate
{
    public string Name => BuiltinRegistry.ScaffoldSpecs;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("scaffold_specs requires a featureKey", "featureKey input missing"));

        var featureKey = request.FeatureKey;
        var requirements = RequirementDtos.ParseJson(GateInputs.GetOptional(request.Inputs, "requirementsJson"));
        var manifestPath = ArtifactPaths.ManifestPath(request.WorkspacePath, featureKey);

        if (File.Exists(manifestPath))
        {
            // A retried attempt (RetryStageAsync — see WorkflowEngine) opens a brand-new
            // business-analyst run for a feature that already scaffolded successfully earlier;
            // its manifest is valid and simply has nothing to do here. Only an unexpected
            // manifest for a DIFFERENT feature key is the "something's wrong" case this refusal
            // exists to catch.
            var existing = SliceManifestIO.TryRead(manifestPath);
            if (existing is not null && string.Equals(existing.Feature, featureKey, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(GateResult.Pass(
                    $"Scaffold for '{featureKey}' already exists", "nothing to change"));

            return Task.FromResult(GateResult.Fail(
                $"Scaffold already exists at {manifestPath}",
                "Refusing to overwrite existing scaffold. Remove the manifest to re-scaffold."));
        }

        var title = GateInputs.Get(request.Inputs, "title", featureKey);
        // An app that doesn't split into backend/frontend (single WPF/console/library project) —
        // or a business-analyst conversation that agreed on a different hierarchy — supplies its
        // own free-form list instead of the classic two-slot split.
        var codePaths = GateInputs.GetList(request.Inputs, "codePaths");
        // Mirrors codePaths above: a shared core that doesn't split into "back"/"front" either
        // supplies its own free-form list instead of the classic corePathBack/corePathFront pair.
        var corePaths = GateInputs.GetList(request.Inputs, "corePaths");
        var manifest = codePaths.Count > 0
            ? new SliceManifest(
                featureKey,
                title,
                codePaths,
                GateInputs.GetList(request.Inputs, "sharedFiles"),
                GateInputs.Get(request.Inputs, "testCommand", "dotnet test DevTeam.slnx"),
                GateInputs.Get(request.Inputs, "corePathBack", CorePaths.DefaultBack),
                GateInputs.Get(request.Inputs, "corePathFront", CorePaths.DefaultFront))
                {
                    CorePaths = corePaths.Count > 0 ? corePaths.ToList() : [],
                }
            : new SliceManifest(
                featureKey,
                title,
                GateInputs.Get(request.Inputs, "codePathBack", CodePathDefaults.DefaultBack),
                GateInputs.Get(request.Inputs, "codePathFront", CodePathDefaults.DefaultFront),
                GateInputs.GetList(request.Inputs, "sharedFiles"),
                GateInputs.Get(request.Inputs, "testCommand", "dotnet test DevTeam.slnx"),
                GateInputs.Get(request.Inputs, "corePathBack", CorePaths.DefaultBack),
                GateInputs.Get(request.Inputs, "corePathFront", CorePaths.DefaultFront));

        cancellationToken.ThrowIfCancellationRequested();
        SliceManifestIO.Write(manifestPath, manifest);
        WriteRequirements(request.WorkspacePath, featureKey, title, requirements);
        WriteProjectProfileOnce(request);

        var artifacts =
            $"{Path.GetRelativePath(request.WorkspacePath, manifestPath)}" + Environment.NewLine +
            $"{Path.GetRelativePath(request.WorkspacePath, ArtifactPaths.BrsPath(request.WorkspacePath, featureKey))}";
        return Task.FromResult(GateResult.Pass(
            $"Scaffolded feature '{featureKey}' with {requirements.Count} requirement(s)",
            artifacts,
            Path.GetRelativePath(request.WorkspacePath, manifestPath)));
    }

    // Machine-written once, from whichever feature's business-analyst conversation first settles
    // on an app type and/or a non-default code hierarchy — a later feature scaffolding into the
    // same workspace must never overwrite it (see ScaffoldSpecsGateTests.ProfileAlreadyExists_*).
    // The classic backend/frontend split (no projectType, no free-form codePaths) has nothing
    // non-default to declare, so it deliberately leaves the profile absent — see the precedence
    // rule in ReadinessProfileLoader: no profile means convention-sniffing runs unchanged.
    private static void WriteProjectProfileOnce(GateRequest request)
    {
        if (ProjectProfileIO.Exists(request.WorkspacePath))
            return;

        var projectType = GateInputs.GetOptional(request.Inputs, "projectType");
        var codePaths = GateInputs.GetList(request.Inputs, "codePaths");
        if (projectType is null && codePaths.Count == 0)
            return;

        ProjectProfileIO.Write(request.WorkspacePath, new ProjectProfile
        {
            ProjectType = projectType ?? string.Empty,
            CorePaths = GateInputs.GetList(request.Inputs, "corePaths").ToList(),
            BuildCommand = GateInputs.GetOptional(request.Inputs, "buildCommand"),
            TestCommand = GateInputs.GetOptional(request.Inputs, "testCommand"),
        });
    }

    private static void WriteRequirements(string workspacePath, string featureKey, string title, IReadOnlyList<RequirementDtos.Requirement> requirements)
    {
        var path = ArtifactPaths.BrsPath(workspacePath, featureKey);
        if (File.Exists(path))
        {
            // The business-analyst may have authored the BRS during the conversation (e.g. on a
            // reworked attempt). Never clobber an existing contract file with scaffold defaults.
            return;
        }

        var builder = new StringBuilder();
        builder.Append($"# {featureKey} — {title}").AppendLine();
        builder.AppendLine();
        if (requirements.Count == 0)
        {
            builder.AppendLine("(No requirements yet — add REQ entries below, each with Gherkin acceptance criteria.)");
        }
        else
        {
            foreach (var requirement in requirements)
            {
                builder.Append($"## {requirement.Id}: {requirement.Title}").AppendLine();
                builder.AppendLine();
                if (!string.IsNullOrWhiteSpace(requirement.AcceptanceCriteria))
                    builder.AppendLine(requirement.AcceptanceCriteria);
                else
                    builder.AppendLine("[Acceptance criteria TBD — replace with Given/When/Then]");
                builder.AppendLine();
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString());
    }
}