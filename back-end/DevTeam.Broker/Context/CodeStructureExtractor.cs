using System.Text;
using System.Text.RegularExpressions;

namespace DevTeam.Broker.Context;

/// <summary>
/// Builds <c>structure.md</c> by reading source files with code — never a model — so the facts
/// in it cannot be hallucinated (spec REQ-008). Still regex-based, so every section is labelled
/// approximate (§12.16): for the .NET/TS stack this workspace uses it is good enough, and for
/// anything else it says so honestly rather than pretending.
/// </summary>
public static partial class CodeStructureExtractor
{
    private const int MaxFilesScanned = 8000;

    private static readonly HashSet<string> ExcludedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", "node_modules", "bin", "obj", ".next", "dist", "out", "coverage", "devteam",
    };

    public static string Build(string workspacePath)
    {
        if (!Directory.Exists(workspacePath))
            return "## Structure\n\nNo automatic structure available (workspace not found).";

        var csFiles = new List<string>();
        var tsFiles = new List<string>();

        foreach (var file in SafeEnumerate(workspacePath))
        {
            if (csFiles.Count + tsFiles.Count >= MaxFilesScanned)
                break;

            if (IsExcluded(file, workspacePath))
                continue;

            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".cs":
                    csFiles.Add(file);
                    break;
                case ".ts":
                case ".tsx":
                    tsFiles.Add(file);
                    break;
            }
        }

        if (csFiles.Count == 0 && tsFiles.Count == 0)
            return "## Structure\n\nNo automatic structure available (no .NET or TypeScript sources found). " +
                   "The compressed pack and the map are the overview for this workspace.";

        var entities = ExtractEntities(csFiles);
        var routes = ExtractRoutes(csFiles);
        var types = ExtractTypes(tsFiles);

        var builder = new StringBuilder();
        builder.AppendLine("# Structure (automatic — approximate; verify against real files)").AppendLine();

        builder.AppendLine("## Entities");
        if (entities.DbSetNames.Count == 0)
        {
            builder.AppendLine("- (none found)");
        }
        else
        {
            builder.AppendLine($"- {string.Join(", ", entities.DbSetNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");
            foreach (var relation in entities.Relationships)
                builder.AppendLine($"- {relation}");
        }
        builder.AppendLine();

        builder.AppendLine("## API routes");
        if (routes.Count == 0)
            builder.AppendLine("- (none found)");
        else
            foreach (var route in routes)
                builder.AppendLine($"- {route}");
        builder.AppendLine();

        builder.AppendLine("## TypeScript types");
        if (types.Count == 0)
            builder.AppendLine("- (none found)");
        else
            foreach (var type in types)
                builder.AppendLine($"- {type}");
        builder.AppendLine();

        builder.AppendLine("## Sources");
        builder.AppendLine($"- C# files: {csFiles.Count}");
        builder.AppendLine($"- TypeScript files: {tsFiles.Count}");

        return builder.ToString();
    }

    private static (HashSet<string> DbSetNames, List<string> Relationships) ExtractEntities(IReadOnlyList<string> csFiles)
    {
        var dbSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perFile = new List<(string File, string Text)>();

        foreach (var file in csFiles)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            perFile.Add((file, text));
            foreach (Match match in DbSetPattern().Matches(text))
                dbSets.Add(match.Groups["type"].Value);
        }

        var relationships = new List<string>();
        foreach (var (file, text) in perFile)
        {
            var declaring = TypePattern().Match(text).Groups["name"].Value;
            if (declaring.Length == 0 || !dbSets.Contains(declaring))
                continue;

            foreach (Match match in CollectionPattern().Matches(text))
            {
                var related = match.Groups["type"].Value;
                if (dbSets.Contains(related))
                    relationships.Add($"{declaring} *—* {related}");
            }
        }

        return (dbSets, relationships.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static List<string> ExtractRoutes(IReadOnlyList<string> csFiles)
    {
        var routes = new List<string>();
        foreach (var file in csFiles)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            foreach (Match match in RoutePattern().Matches(text))
            {
                var verb = match.Groups["verb"].Value.ToUpperInvariant();
                var route = match.Groups["route"].Value;
                routes.Add($"{verb} {route}");
            }
        }

        return routes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractTypes(IReadOnlyList<string> tsFiles)
    {
        var types = new List<string>();
        foreach (var file in tsFiles)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            foreach (Match match in TsTypePattern().Matches(text))
                types.Add(match.Groups["name"].Value);
        }

        return types.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SafeEnumerate(string workspacePath)
    {
        try
        {
            return Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsExcluded(string file, string workspacePath)
    {
        var relative = Path.GetRelativePath(workspacePath, file).Replace('\\', '/');
        return relative.Split('/').Any(ExcludedSegments.Contains);
    }

    private static string? TryRead(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"DbSet<(?<type>[A-Za-z_]\w*)>", RegexOptions.Compiled)]
    private static partial Regex DbSetPattern();

    [GeneratedRegex(@"\b(?:class|record)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"\b(?:ICollection|IEnumerable|List|HashSet)<(?<type>[A-Za-z_]\w*)>", RegexOptions.Compiled)]
    private static partial Regex CollectionPattern();

    [GeneratedRegex(@"Map(?<verb>Get|Post|Put|Delete|Patch)\s*\(\s*""(?<route>[^""]*)""", RegexOptions.Compiled)]
    private static partial Regex RoutePattern();

    [GeneratedRegex(@"export\s+(?:interface|type)\s+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex TsTypePattern();
}

/// <summary>Reads the file list out of a Repomix XML pack without loading the whole thing into
/// memory as a DOM — a streaming line scan keeps a multi-megabyte file cheap.</summary>
public static partial class PackReader
{
    public static IReadOnlyList<string> ReadFilePaths(string packPath)
    {
        if (!File.Exists(packPath))
            return [];

        var paths = new List<string>();
        try
        {
            using var reader = new StreamReader(packPath);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var match = FileLine().Match(line);
                if (match.Success)
                    paths.Add(match.Groups["path"].Value);
            }
        }
        catch (IOException)
        {
            return paths;
        }

        return paths;
    }

    [GeneratedRegex(@"<file\s+path=""(?<path>[^""]+)""", RegexOptions.Compiled)]
    private static partial Regex FileLine();
}
