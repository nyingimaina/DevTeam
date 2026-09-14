using System.Text.Json;

namespace DevTeam.Broker.Gates;

public sealed record TestFileInfo(string Path, string Content);

public static class TestDiscovery
{
    private const int DefaultMaxFiles = 200;
    private const int DefaultMaxCharsPerFile = 16_384;

    private static readonly string[] ExcludedDirectorySegments = [".git", "bin", "obj", "node_modules", "dist", ".next", "artifacts", "packages"];

    public static IReadOnlyList<TestFileInfo> Discover(string workspacePath, int maxFiles = DefaultMaxFiles, int maxCharsPerFile = DefaultMaxCharsPerFile)
    {
        if (!Directory.Exists(workspacePath))
            return [];

        var results = new List<TestFileInfo>();
        foreach (var file in Directory.EnumerateFiles(workspacePath, "*", SearchOption.AllDirectories))
        {
            if (results.Count >= maxFiles)
                break;

            var relative = Path.GetRelativePath(workspacePath, file);
            if (IsExcluded(relative) || !LooksLikeTestFile(relative))
                continue;

            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            if (content.Length > maxCharsPerFile)
                content = content[..maxCharsPerFile];
            results.Add(new TestFileInfo(relative, content));
        }

        return results;
    }

    public static bool LooksLikeTestFile(string relativePath)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || s.Equals("tests", StringComparison.OrdinalIgnoreCase)))
            return true;

        var name = Path.GetFileName(relativePath).ToLowerInvariant();
        return name.EndsWith("tests.cs", StringComparison.Ordinal)
            || name.EndsWith("tests.ts", StringComparison.Ordinal)
            || name.EndsWith("tests.tsx", StringComparison.Ordinal)
            || name.EndsWith("tests.js", StringComparison.Ordinal)
            || name.EndsWith("tests.jsx", StringComparison.Ordinal)
            || name.Contains(".tests.", StringComparison.Ordinal)
            || name.Contains(".test.", StringComparison.Ordinal)
            || name.Contains(".spec.", StringComparison.Ordinal);
    }

    private static bool IsExcluded(string relativePath)
    {
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(seg => ExcludedDirectorySegments.Contains(seg, StringComparer.OrdinalIgnoreCase));
    }
}

public static class TestFiles
{
    public static string Text(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            var files = JsonSerializer.Deserialize<List<TestFileInfo>>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (files is null || files.Count == 0)
                return string.Empty;

            var builder = new System.Text.StringBuilder();
            foreach (var file in files)
            {
                builder.Append(file.Path).Append(' ').AppendLine(file.Content);
            }

            return builder.ToString();
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}