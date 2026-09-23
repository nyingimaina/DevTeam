using System.Text.RegularExpressions;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Materialises the workspace's one shared build project (and the solution that references it)
/// exactly once, before any feature code is written. Without this the developer agent has no
/// project to extend, so — needing something to build and test — it scaffolds a project inside
/// its own feature slice, which is how one <c>.csproj</c> per feature gets produced.
///
/// The project lives at the root of the workspace's backend tree (the parent of the resolved
/// core directory — <c>back-end/src</c> by default) so every feature folder and the shared core
/// compile into that single project. It is deliberately a single project: tests live in the same
/// project as the code (xUnit references), matching the "one project per app" layout.
///
/// Idempotent and non-destructive: if the workspace already contains a build project or solution
/// (an imported repo), nothing is written — DevTeam extends the existing app instead.
/// </summary>
public sealed class CoreScaffoldGate : IGate
{
    // Same versions the platform's own test projects use, so a freshly scaffolded workspace
    // restores from the same packages and behaves identically to a hand-authored one.
    private const string ProjectTemplate =
        "<Project Sdk=\"Microsoft.NET.Sdk\">" + NewLine +
        NewLine +
        "  <PropertyGroup>" + NewLine +
        "    <TargetFramework>net10.0</TargetFramework>" + NewLine +
        "    <Nullable>enable</Nullable>" + NewLine +
        "    <ImplicitUsings>enable</ImplicitUsings>" + NewLine +
        "    <IsPackable>false</IsPackable>" + NewLine +
        "  </PropertyGroup>" + NewLine +
        NewLine +
        "  <ItemGroup>" + NewLine +
        "    <PackageReference Include=\"coverlet.collector\" Version=\"6.0.4\" />" + NewLine +
        "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.14.1\" />" + NewLine +
        "    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />" + NewLine +
        "    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.4\" />" + NewLine +
        "  </ItemGroup>" + NewLine +
        NewLine +
        "  <ItemGroup>" + NewLine +
        "    <Using Include=\"Xunit\" />" + NewLine +
        "  </ItemGroup>" + NewLine +
        NewLine +
        "</Project>" + NewLine;

    private const string NewLine = "\n";

    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".esproj", ".vcxproj",
    };

    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "devteam", "bin", "obj", "node_modules", "publish", "TestResults", ".next",
    };

    private static readonly Regex NotIdentifier = new("[^A-Za-z0-9]+", RegexOptions.Compiled);

    public string Name => BuiltinRegistry.CoreScaffold;

    public Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.FeatureKey))
            return Task.FromResult(GateResult.Fail("core_scaffold requires a featureKey", "featureKey input missing"));

        var manifestPath = ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey);
        var manifest = SliceManifestIO.TryRead(manifestPath);
        if (manifest is null)
            return Task.FromResult(GateResult.Pass(
                "No feature manifest — nothing to scaffold yet",
                "manifest missing, skipping core scaffold (context_bundle will require it)"));

        var featureKey = request.FeatureKey;
        if (FindExistingProject(request.WorkspacePath, manifest) is { } existing)
            return Task.FromResult(GateResult.Pass(
                "Workspace already ships a build project — extending it",
                $"found {existing}; no scaffold written"));

        var coreBack = CorePaths.Back(manifest);
        var projectRoot = ParentRelative(coreBack);
        var appName = DeriveProjectName(request.WorkspacePath);
        var projectRelative = JoinRelative(projectRoot, appName + ".csproj");
        var solutionRelative = "DevTeam.slnx";

        var created = new List<string>();
        WriteIfAbsent(request.WorkspacePath, projectRelative, ProjectTemplate, created);
        WriteIfAbsent(
            request.WorkspacePath,
            solutionRelative,
            "<Solution>" + NewLine + "  <Project Path=\"" + projectRelative + "\" />" + NewLine + "</Solution>" + NewLine,
            created);

        var updated = ApplyManifestChanges(manifest, coreBack, projectRoot, projectRelative);
        if (updated)
            SliceManifestIO.Save(manifestPath, manifest);

        return Task.FromResult(GateResult.Pass(
            $"Scaffolded one shared project for '{featureKey}'",
            (created.Count == 0 ? "project already present" : "created " + string.Join(", ", created)) +
                (updated ? $" (feature slice → {manifest.CodePathBack})" : string.Empty),
            projectRelative));
    }

    /// <summary>
    /// The single project's name, derived from the workspace folder so the app reads like the
    /// project it belongs to (<c>calculator</c> → <c>Calculator</c>). Falls back to <c>App</c>
    /// when the folder has no usable characters.
    /// </summary>
    public static string DeriveProjectName(string workspacePath)
    {
        var normalized = (workspacePath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        var folder = normalized.Length == 0 ? string.Empty : normalized[(normalized.LastIndexOf('/') + 1)..];
        var name = string.Concat(
            NotIdentifier.Split(folder)
                .Where(part => part.Length > 0)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (name.Length == 0)
            return "App";
        return char.IsDigit(name[0]) ? "App" + name : name;
    }

    // A build project anywhere outside a feature slice means the repo already has its shared
    // app — the one thing this gate exists to provide. Project files inside a slice are ignored
    // here (they are the bug's artifacts) so a legacy workspace still gets a shared project;
    // ProjectStructureGate then reports the strays.
    private static string? FindExistingProject(string workspacePath, SliceManifest manifest)
    {
        if (!Directory.Exists(workspacePath))
            return null;

        var templates = manifest.EffectiveCodePaths;
        foreach (var file in EnumerateFiles(workspacePath))
        {
            if (!ProjectExtensions.Contains(Path.GetExtension(file)))
                continue;

            var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
            // Anything that is *not* inside the feature's own slice counts as the workspace's
            // existing project — including a shared core project, which is exactly the one we
            // want to keep rather than scaffold a duplicate of.
            if (SliceAllowlist.IsInSlice(relative, manifest.Feature, templates))
                continue;

            return relative;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFiles(string workspacePath)
        => Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories)
            .Where(file => !HasExcludedSegment(file, workspacePath));

    private static bool HasExcludedSegment(string file, string workspacePath)
    {
        var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
        return relative.Split('/').Any(ExcludedSegments.Contains);
    }

    private static void WriteIfAbsent(string workspacePath, string relative, string content, List<string> created)
    {
        var path = Path.Combine(workspacePath, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        created.Add(relative);
    }

    private static bool ApplyManifestChanges(SliceManifest manifest, string coreBack, string projectRoot, string projectRelative)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(manifest.CorePathBack))
        {
            manifest.CorePathBack = coreBack;
            changed = true;
        }

        // Keep the generic platform default ("back-end/**/Features/<F>") from landing feature
        // folders outside the single project. A deliberately custom path is left alone.
        if (manifest.CodePathBack.Contains("**", StringComparison.Ordinal))
        {
            manifest.CodePathBack = JoinRelative(projectRoot, "Features/<F>");
            changed = true;
        }

        if (!manifest.Shared.Contains(projectRelative, StringComparer.OrdinalIgnoreCase))
        {
            manifest.Shared.Add(projectRelative);
            changed = true;
        }

        return changed;
    }

    // "back-end/src/Core" → "back-end/src"; a bare folder ("Core") → "" (the workspace root).
    private static string ParentRelative(string relative)
    {
        var normalized = relative.Replace('\\', '/').Trim('/');
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }

    private static string JoinRelative(string directory, string child)
        => string.IsNullOrEmpty(directory) ? child : directory + "/" + child;
}
