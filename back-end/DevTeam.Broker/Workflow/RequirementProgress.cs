using DevTeam.Broker.Gates;

namespace DevTeam.Broker.Workflow;

public sealed record ProgressCount(int Done, int Total);

/// <summary>How far the work has got against the BRS: code that names a requirement, and tests that do.</summary>
public sealed record RequirementProgress(int Requirements, ProgressCount Code, ProgressCount Tests);

/// <summary>
/// Deterministic progress against the requirement list. There is no model in the loop: a
/// requirement counts toward "tests" when a test names it (the same matcher the coverage gate
/// uses) and toward "code" when a non-test source file is tagged with it (e.g. <c>// REQ-3</c>).
/// So the bars agree with the gate, and can't be inflated by an optimistic agent.
/// </summary>
public static class RequirementProgressCalculator
{
    private const int MaxCharsPerFile = 16_384;
    private const int MaxFiles = 4000;

    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "devteam", "bin", "obj", "node_modules", "publish",
        "TestResults", ".next", "dist", "out", "build", "coverage", "artifacts",
    };

    private static readonly string[] SourceExtensions =
    [
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".java", ".kt", ".py", ".go", ".rb", ".rs", ".swift", ".php",
    ];

    public static RequirementProgress Compute(string workspacePath, string featureKey)
    {
        var requirements = RequirementsExtractor.Extract(workspacePath, featureKey);
        if (requirements.Count == 0)
            return new RequirementProgress(0, new ProgressCount(0, 0), new ProgressCount(0, 0));

        // Scope to this feature's own slice. Requirement ids are per-feature, so a whole-workspace
        // scan lets one feature's tests (and code) satisfy another feature's requirements.
        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspacePath, featureKey));
        var testCorpus = BuildTestCorpus(workspacePath, featureKey, manifest);
        var codeCorpus = BuildSourceCorpus(workspacePath, featureKey, manifest);

        var testsDone = requirements.Count(requirement => RequirementMatcher.MatchesRequirement(testCorpus, requirement));
        var codeDone = requirements.Count(requirement => RequirementMatcher.MatchesId(codeCorpus, requirement.Id));

        return new RequirementProgress(
            requirements.Count,
            new ProgressCount(codeDone, requirements.Count),
            new ProgressCount(testsDone, requirements.Count));
    }

    private static string BuildTestCorpus(string workspacePath, string featureKey, SliceManifest? manifest)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var file in FeatureTestFiles.Discover(workspacePath, featureKey, manifest))
            builder.Append(file.Path).Append(' ').AppendLine(file.Content);
        return builder.ToString();
    }

    private static string BuildSourceCorpus(string workspacePath, string featureKey, SliceManifest? manifest)
    {
        var builder = new System.Text.StringBuilder();
        if (!Directory.Exists(workspacePath))
            return builder.ToString();

        var templates = FeatureTestFiles.SliceTemplates(manifest);
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories))
        {
            if (seen >= MaxFiles)
                break;

            if (!SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                continue;
            if (HasExcludedSegment(file, workspacePath))
                continue;

            var relative = Path.GetRelativePath(workspacePath, file);
            if (TestDiscovery.LooksLikeTestFile(relative))
                continue;
            if (templates is not null && !SliceAllowlist.IsInSlice(relative, featureKey, templates))
                continue;

            try
            {
                var content = File.ReadAllText(file);
                builder.Append(relative).Append('\n');
                builder.AppendLine(content.Length > MaxCharsPerFile ? content[..MaxCharsPerFile] : content);
                seen++;
            }
            catch (IOException)
            {
                // Unreadable file simply doesn't contribute.
            }
        }

        return builder.ToString();
    }

    private static bool HasExcludedSegment(string file, string workspacePath)
        => Path.GetRelativePath(workspacePath, file)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(ExcludedSegments.Contains);
}
