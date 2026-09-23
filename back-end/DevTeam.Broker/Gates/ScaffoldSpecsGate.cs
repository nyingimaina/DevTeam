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
        var manifest = new SliceManifest(
            featureKey,
            title,
            GateInputs.Get(request.Inputs, "codePathBack", "back-end/**/Features/<F>"),
            GateInputs.Get(request.Inputs, "codePathFront", "front-end/app/<F>"),
            GateInputs.GetList(request.Inputs, "sharedFiles"),
            GateInputs.Get(request.Inputs, "testCommand", "dotnet test DevTeam.slnx"),
            GateInputs.Get(request.Inputs, "corePathBack", CorePaths.DefaultBack),
            GateInputs.Get(request.Inputs, "corePathFront", CorePaths.DefaultFront));

        cancellationToken.ThrowIfCancellationRequested();
        SliceManifestIO.Write(manifestPath, manifest);
        WriteRequirements(request.WorkspacePath, featureKey, title, requirements);

        var artifacts =
            $"{Path.GetRelativePath(request.WorkspacePath, manifestPath)}" + Environment.NewLine +
            $"{Path.GetRelativePath(request.WorkspacePath, ArtifactPaths.BrsPath(request.WorkspacePath, featureKey))}";
        return Task.FromResult(GateResult.Pass(
            $"Scaffolded feature '{featureKey}' with {requirements.Count} requirement(s)",
            artifacts,
            Path.GetRelativePath(request.WorkspacePath, manifestPath)));
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