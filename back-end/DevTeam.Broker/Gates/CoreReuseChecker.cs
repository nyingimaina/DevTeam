using System.Text.RegularExpressions;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Static analysis that flags a feature slice for duplicating code the shared core already
/// provides (same file basename, same type name, or a scaffolded sibling app via a
/// duplicate .csproj). The core app ships once per workspace and every feature is expected to
/// extend it — so a feature file that shadows a core file is the strongest signal that the
/// agent wrote a parallel implementation instead of reusing. Also exposes the core directories
/// for the agent context map (see ContextBundleGate).
/// </summary>
public static class CoreReuseChecker
{
    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "devteam", "bin", "obj", "node_modules", "publish", "TestResults",
    };

    // Next.js scaffolds per-routed-route files with these names; a feature legitimately owns
    // its own page.tsx / layout.tsx / etc. alongside the core's, so they are not duplications.
    private static readonly HashSet<string> GenericFrontendNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "page", "layout", "loading", "error", "not-found", "route", "template", "default",
    };

    private static readonly string[] BackendExtensions = [".cs", ".cshtml", ".razor", ".csproj"];
    private static readonly string[] FrontendExtensions = [".ts", ".tsx", ".js", ".jsx", ".css", ".scss", ".html"];

    private static readonly Regex CSharpType = new(
        @"\b(?:class|record|struct|interface)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);
    private static readonly Regex FrontendDecl = new(
        @"\b(?:class|interface|type|function)\s+([A-Za-z_]\w*)", RegexOptions.Compiled);

    public static IReadOnlyList<string> CoreSourcePaths(string workspacePath, SliceManifest? manifest)
    {
        var paths = new List<string>(2);
        AddIfExists(Paths(workspacePath, CorePaths.Back(manifest)));
        AddIfExists(Paths(workspacePath, CorePaths.Front(manifest)));
        return paths;

        void AddIfExists(string path)
        {
            if (Directory.Exists(path))
                paths.Add(path);
        }
    }

    /// <summary>
    /// Returns human-readable violations for this feature's slice, empty when the feature
    /// extends the core without duplicating it.
    /// </summary>
    public static IReadOnlyList<string> FindViolations(string workspacePath, SliceManifest? manifest)
    {
        if (manifest is null)
            return [];

        var coreBack = Normed(CorePaths.Back(manifest));
        var coreFront = Normed(CorePaths.Front(manifest));
        var backTemplate = manifest.CodePathBack;
        var frontTemplate = manifest.CodePathFront;

        var coreBackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coreBackTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var coreFrontNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Two passes: every core file must be classified before any feature file is compared
        // against it — directory enumeration order is arbitrary, so a single pass would miss a
        // duplicate whenever the feature's copy happens to be visited first.
        var featureBackFiles = new List<(string Relative, string Name, string Type)>();
        var featureFrontFiles = new List<(string Relative, string Name, string Ext)>();

        foreach (var file in EnumerateSourceFiles(workspacePath))
        {
            var relative = Normed(Path.GetRelativePath(workspacePath, file));
            var ext = Path.GetExtension(relative);
            if (IsBackendPlaceholder(file))
                continue;

            var name = Path.GetFileNameWithoutExtension(relative);

            if (IsWithin(relative, coreBack))
            {
                coreBackNames.Add(name);
                if (ext == ".cs")
                    coreBackTypes[name] = CSharpType.Match(File.ReadAllText(file)).Groups[1].Value;
                continue;
            }

            if (IsWithin(relative, coreFront))
            {
                coreFrontNames.Add(name);
                continue;
            }

            if (SliceAllowlist.IsAllowed(relative, manifest.Feature, [], new[] { backTemplate }) && BackendExtensions.Contains(ext))
            {
                var type = ext == ".cs" ? CSharpType.Match(File.ReadAllText(file)).Groups[1].Value : string.Empty;
                featureBackFiles.Add((relative, name, type));
            }
            else if (SliceAllowlist.IsAllowed(relative, manifest.Feature, [], new[] { frontTemplate }) && FrontendExtensions.Contains(ext))
            {
                featureFrontFiles.Add((relative, name, ext));
            }
        }

        var violations = new List<string>();
        foreach (var (relative, name, type) in featureBackFiles)
        {
            if (!coreBackNames.Contains(name))
                continue;

            if (Path.GetExtension(relative) == ".csproj")
                violations.Add($"fail: duplicate project {name}.csproj — feature scaffolded a sibling app instead of extending the core ({CorePaths.Back(manifest)})");
            else if (!string.IsNullOrEmpty(type)
                     && coreBackTypes.TryGetValue(name, out var coreType)
                     && string.Equals(coreType, type, StringComparison.OrdinalIgnoreCase))
                violations.Add($"fail: '{relative}' shadow-declares the core type '{type}' — reuse the core type ({CorePaths.Back(manifest)}) instead of re-declaring it");
        }

        foreach (var (relative, name, ext) in featureFrontFiles)
        {
            if (coreFrontNames.Contains(name) && !GenericFrontendNames.Contains(name))
                violations.Add($"fail: '{relative}' shadows core file '{name}{ext}' — reuse the core component ({CorePaths.Front(manifest)}) instead of re-implementing it");
        }

        return violations;
    }

    private static IEnumerable<string> EnumerateSourceFiles(string workspacePath)
        => Directory.Exists(workspacePath)
            ? Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories)
                .Where(file => !HasExcludedSegment(file, workspacePath))
                .Where(file => IsScannable(file))
            : [];

    private static bool IsScannable(string file)
    {
        var ext = Path.GetExtension(file);
        if (!BackendExtensions.Contains(ext) && !FrontendExtensions.Contains(ext))
            return false;
        var dir = Path.GetDirectoryName(file);
        return dir is null || !dir.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBackendPlaceholder(string file)
    {
        var name = Path.GetFileName(file);
        return Path.GetExtension(file) == ".cs"
            && name.EndsWith("AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExcludedSegment(string file, string workspacePath)
    {
        var relative = Path.GetRelativePath(workspacePath, file);
        return relative.Split(['/', '\\']).Any(ExcludedSegments.Contains);
    }

    private static string Normed(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    private static string Paths(string workspacePath, string relative)
        => Normed(Path.Combine(workspacePath, relative));

    private static bool IsWithin(string relative, string root)
        => relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}