namespace DevTeam.Broker.Gates;

/// <summary>
/// Flags a build project or solution that has leaked into a feature slice. A feature slice is
/// meant to hold feature-specific source files only: the app's project files live once in the
/// shared core (see <see cref="CoreScaffoldGate"/>). A project inside a slice is the fingerprint
/// of "one app per feature", so it is always a failure — even when no core exists yet to compare
/// against (unlike <see cref="CoreReuseChecker"/>, which needs a core file to shadow).
/// </summary>
public static class ProjectStructureChecker
{
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "devteam", "bin", "obj", "node_modules", "publish", "TestResults", ".next",
    };

    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".esproj", ".vcxproj",
    };

    public static IReadOnlyList<string> FindViolations(string workspacePath, SliceManifest? manifest)
    {
        if (manifest is null || !Directory.Exists(workspacePath))
            return [];

        var templates = manifest.EffectiveCodePaths;
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories))
        {
            if (!ProjectExtensions.Contains(Path.GetExtension(file)))
                continue;
            if (HasExcludedSegment(file, workspacePath))
                continue;

            var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
            if (SliceAllowlist.IsInSlice(relative, manifest.Feature, templates))
                violations.Add(
                    $"fail: '{relative}' is a build project inside the feature's own folder — features extend the one shared app; " +
                    "keep feature folders to source files and put the project in the shared core");
        }

        return violations;
    }

    private static bool HasExcludedSegment(string file, string workspacePath)
    {
        var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
        return relative.Split('/').Any(ExcludedSegments.Contains);
    }
}
