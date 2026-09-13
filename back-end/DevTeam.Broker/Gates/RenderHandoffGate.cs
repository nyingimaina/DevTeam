using System.Text;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class RenderHandoffGate : IGate
{
    public string Name => BuiltinRegistry.RenderHandoff;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("render_handoff requires a featureKey", "featureKey input missing"));

        var featureKey = request.FeatureKey;
        var roleName = GateInputs.Get(request.Inputs, "roleName", request.RoleName ?? "previous role");
        var nextStage = GateInputs.Get(request.Inputs, "nextStage", "next role");
        var summary = GateInputs.Get(request.Inputs, "summary", "Stage completed.");

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