using System.Text.RegularExpressions;

namespace DevTeam.Broker.Gates;

/// <summary>
/// The changed-file set behind the fast lane, and how it splits by language. Pure functions
/// with no process/IO of their own, so the classification rules are unit-testable without
/// standing up a git repository or a frontend toolchain.
/// </summary>
public static class ChangedFiles
{
    private static readonly string[] TypeScriptExtensions =
        [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".mts", ".cts"];

    private static readonly string[] DotNetExtensions =
        [".cs", ".csproj", ".slnx", ".sln", ".props", ".targets"];

    private const string PackageJson = "package.json";

    public static IReadOnlyList<string> Parse(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return [];

        return rawOutput
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('"'))
            .Where(line => line.Length > 0)
            .ToArray();
    }

    public static bool IsTypeScript(string path) => HasExtension(path, TypeScriptExtensions);

    /// <summary>
    /// Paths out of <c>git status --porcelain</c>: the two status columns and the optional
    /// "old -> new" rename arrow are stripped, so the result is plain workspace-relative paths
    /// just like the <c>--name-only</c> diff output. This is what catches work that isn't
    /// committed yet - which is most of what the developer stage is doing when the fast lane runs.
    /// </summary>
    public static IReadOnlyList<string> ParsePorcelain(string rawOutput)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
            return [];

        var paths = new List<string>();
        foreach (var line in rawOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = PorcelainStatusPrefix.Replace(line, string.Empty);
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                path = path[(arrow + 4)..].Trim('"');
            path = path.Trim().Trim('"');
            if (path.Length > 0)
                paths.Add(path);
        }

        return paths;
    }

    // Porcelain's two status columns, then the space that aligns the path. Matched rather than
    // sliced at a fixed offset, because callers (and the output itself after trimming) may have
    // eaten that alignment space.
    private static readonly Regex PorcelainStatusPrefix = new(
        @"^[\s?MADRCU!]{1,2}\s+",
        RegexOptions.Compiled);

    public static bool IsDotNet(string path) => HasExtension(path, DotNetExtensions);

    /// <summary>
    /// The frontend project that owns the changed TypeScript files: the nearest ancestor of the
    /// first changed source file that has a package.json. Null when the changed TS files belong
    /// to no npm project at all (a stray .ts doc, a vendored file) - the caller then skips the
    /// type/test checks rather than running them in the wrong directory.
    /// </summary>
    public static string? FrontendRoot(string workspacePath, IReadOnlyList<string> changedPaths)
    {
        var source = changedPaths.FirstOrDefault(IsTypeScript);
        if (source is null || !Directory.Exists(workspacePath))
            return null;

        var relative = source.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(Path.Combine(workspacePath, Path.GetDirectoryName(relative) ?? "."));
        var root = Path.GetFullPath(workspacePath);

        while (current.Length >= root.Length)
        {
            if (File.Exists(Path.Combine(current, PackageJson)))
                return current;
            if (current == root)
                break;
            current = Directory.GetParent(current)?.FullName ?? root;
        }

        return null;
    }

    private static bool HasExtension(string path, string[] extensions)
    {
        var extension = Path.GetExtension(path.Replace('\\', '/'));
        return extensions.Any(known => string.Equals(known, extension, StringComparison.OrdinalIgnoreCase));
    }
}
