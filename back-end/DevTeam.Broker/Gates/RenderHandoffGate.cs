using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class RenderHandoffGate : IGate
{
    public string Name => BuiltinRegistry.RenderHandoff;

    // What each stage said it did, in its own words (stage-result.<role>.json). The handoff used to
    // read "Stage completed." because nothing ever supplied a summary.
    private static string StageSummaries(string workspacePath, string featureKey)
    {
        var directory = ArtifactPaths.FeatureDir(workspacePath, featureKey);
        if (!Directory.Exists(directory))
            return "Stage completed.";

        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "stage-result.*.json").Order(StringComparer.Ordinal))
        {
            var role = Path.GetFileNameWithoutExtension(file)["stage-result.".Length..];
            var read = StageResultIO.TryRead(workspacePath, featureKey, role);
            if (read.Ok)
                lines.Add($"- **{role}** ({read.Result!.Verdict.ToString().ToLowerInvariant()}): {read.Result.Summary}");
        }

        return lines.Count == 0 ? "Stage completed." : string.Join(Environment.NewLine, lines);
    }

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("render_handoff requires a featureKey", "featureKey input missing"));

        var featureKey = request.FeatureKey;
        var roleName = GateInputs.Get(request.Inputs, "roleName", request.RoleName ?? "previous role");
        var nextStage = GateInputs.Get(request.Inputs, "nextStage", "next role");
        var summary = GateInputs.Get(request.Inputs, "summary", StageSummaries(request.WorkspacePath, featureKey));

        var builder = new StringBuilder();
        builder.Append($"# Handoff — {featureKey}").AppendLine();
        builder.AppendLine();
        builder.Append("- Completed stage: ").AppendLine(roleName);
        builder.Append("- Next stage: ").AppendLine(nextStage);
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine(summary);
        builder.AppendLine();
        builder.AppendLine("## Artifacts");
        var artifactsDir = ArtifactPaths.FeatureDir(request.WorkspacePath, featureKey);
        if (Directory.Exists(artifactsDir))
        {
            foreach (var file in Directory.EnumerateFiles(artifactsDir, "*", SearchOption.AllDirectories))
                builder.AppendLine($"- `{Path.GetRelativePath(request.WorkspacePath, file)}`");
        }
        else
        {
            builder.AppendLine("- _(no artifacts)_");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var path = ArtifactPaths.HandoffPath(request.WorkspacePath, featureKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, builder.ToString());

        return Task.FromResult(GateResult.Pass(
            "Rendered handoff for the next stage",
            Path.GetRelativePath(request.WorkspacePath, path),
            Path.GetRelativePath(request.WorkspacePath, path)));
    }
}