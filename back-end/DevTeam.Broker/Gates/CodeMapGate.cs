using System.Text;
using System.Text.RegularExpressions;

using DevTeam.Broker.Context;
using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Leading builtin that hands each stage the workspace's short project overview. It is purely
/// informational: it always passes, and when there is no overview yet the stage simply starts
/// without one (spec REQ-010). The whole pack is never injected — only the small map, plus the
/// entries that fall inside this feature's slice.
/// </summary>
public sealed partial class CodeMapGate : IGate
{
    public const string MapFileName = "codemap.md";
    public const string SliceFileName = "codeslice.xml";
    private const int MaxSliceChars = 2_000_000;

    private readonly IRepoContextService? _service;

    public CodeMapGate(IRepoContextService? service = null) => _service = service;

    public string Name => BuiltinRegistry.CodeMap;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return GateResult.Pass("No feature — nothing to provide", "featureKey input missing");

        if (_service is not null && !_service.Enabled)
            return GateResult.Pass("Project overview is turned off", "disabled");

        var index = RepoContextStore.ReadCurrent(request.WorkspacePath);
        var meta = index is null ? null : RepoContextStore.ReadMeta(request.WorkspacePath, index.Dir);
        if (index is null || meta is null)
            return GateResult.Pass("No project overview yet", "no overview has been built for this workspace");

        var mapPath = RepoContextStore.MapPath(request.WorkspacePath, index.Dir);
        var map = TryRead(mapPath) ?? TryRead(RepoContextStore.StructurePath(request.WorkspacePath, index.Dir));
        if (string.IsNullOrWhiteSpace(map))
            return GateResult.Pass("No project overview yet", "overview files were empty");

        var banner = await BuildBannerAsync(request.WorkspacePath, meta, cancellationToken);
        var featureDir = ArtifactPaths.FeatureDir(request.WorkspacePath, request.FeatureKey);
        Directory.CreateDirectory(featureDir);
        File.WriteAllText(Path.Combine(featureDir, MapFileName), banner + "\n\n" + map);

        var sliceWritten = TryWriteSlice(request.WorkspacePath, request.FeatureKey, index.Dir);

        var evidence = $"map from {RepoContextStore.CommitDirName(meta.Commit)}" +
                       (banner.Contains("changes ago", StringComparison.Ordinal) ? " (behind)" : string.Empty) +
                       (sliceWritten ? " + slice" : string.Empty);
        return GateResult.Pass("Provided the project overview to this stage", evidence,
            Path.Combine("devteam", "features", request.FeatureKey, MapFileName));
    }

    private async Task<string> BuildBannerAsync(string workspacePath, RepoContextMeta meta, CancellationToken ct)
    {
        if (_service is null)
            return "> **Project overview (baseline).** This overview may not match the latest code. " +
                   "If it disagrees with the real files, trust the files.";

        var status = await _service.GetStatusAsync(workspacePath, ct);
        return status.State switch
        {
            RepoContextState.UpToDate =>
                "> **Project overview (baseline).** This overview matches the current code.",
            RepoContextState.Behind when status.ChangesBehind is { } behind and > 0 =>
                $"> **Project overview (baseline).** This overview was made {behind} changes ago. " +
                "If it disagrees with the real files, trust the files.",
            RepoContextState.Behind =>
                "> **Project overview (baseline).** This overview may be out of date (the number of changes is unknown). " +
                "If it disagrees with the real files, trust the files.",
            _ =>
                "> **Project overview (baseline).** This overview may not match the latest code. " +
                "If it disagrees with the real files, trust the files.",
        };
    }

    private static bool TryWriteSlice(string workspacePath, string featureKey, string dir)
    {
        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
        if (manifest is null)
            return false;

        var packPath = RepoContextStore.CompressedPackPath(workspacePath, dir);
        var pack = TryRead(packPath);
        if (string.IsNullOrWhiteSpace(pack))
            return false;

        var templates = manifest.EffectiveCodePaths;
        var builder = new StringBuilder("<slice>\n");
        var written = 0;

        foreach (Match match in FileBlock().Matches(pack))
        {
            var path = match.Groups["path"].Value.Replace('\\', '/');
            if (!SliceAllowlist.IsAllowed(path, featureKey, manifest.Shared, templates))
                continue;

            if (builder.Length + match.Length > MaxSliceChars)
                break;

            builder.Append(match.Value).Append('\n');
            written++;
        }

        if (written == 0)
            return false;

        builder.Append("</slice>\n");
        File.WriteAllText(Path.Combine(ArtifactPaths.FeatureDir(workspacePath, featureKey), SliceFileName), builder.ToString());
        return true;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<file\s+path=""(?<path>[^""]+)"">.*?</file>", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex FileBlock();
}
