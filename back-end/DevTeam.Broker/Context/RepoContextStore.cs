using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevTeam.Broker.Context;

/// <summary>The pointer written to <c>devteam/context/current.json</c>.</summary>
public sealed record RepoContextIndex(string Commit, string Dir, DateTimeOffset? UpdatedAtUtc);

/// <summary>The stamp written next to every pack (spec REQ-003).</summary>
public sealed class RepoContextMeta
{
    public int SchemaVersion { get; set; } = 1;

    public string WorkspacePath { get; set; } = string.Empty;

    public string Commit { get; set; } = string.Empty;

    public string Branch { get; set; } = string.Empty;

    public bool Dirty { get; set; }

    public DateTimeOffset BuiltAtUtc { get; set; }

    public string Trigger { get; set; } = string.Empty;

    public string? RepomixVersion { get; set; }

    public Dictionary<string, long> Files { get; set; } = [];

    public int ApproxMapTokens { get; set; }

    public List<string> Warnings { get; set; } = [];
}

/// <summary>
/// All filesystem knowledge for the code overview in one place — paths, atomic pointer writes,
/// the local git exclude and pruning. No process/git logic lives here, so it is pure and easy to
/// unit-test against a temp directory. Everything is workspace-relative: the index must live
/// inside the workspace so the agent can read it (spec REQ-004, §4 fact 1).
/// </summary>
public static class RepoContextStore
{
    public const string RelativeRoot = "devteam/context";
    public const string ExcludeLine = "devteam/context/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string ContextRoot(string workspacePath)
        => Path.Combine(workspacePath, "devteam", "context");

    public static string CurrentJsonPath(string workspacePath)
        => Path.Combine(ContextRoot(workspacePath), "current.json");

    public static string CommitDir(string workspacePath, string dir)
        => Path.Combine(ContextRoot(workspacePath), dir);

    public static string MetaPath(string workspacePath, string dir)
        => Path.Combine(CommitDir(workspacePath, dir), "meta.json");

    public static string MapPath(string workspacePath, string dir)
        => Path.Combine(CommitDir(workspacePath, dir), "CODEBASE_MAP.md");

    public static string StructurePath(string workspacePath, string dir)
        => Path.Combine(CommitDir(workspacePath, dir), "structure.md");

    public static string PackPath(string workspacePath, string dir)
        => Path.Combine(CommitDir(workspacePath, dir), "pack.xml");

    public static string CompressedPackPath(string workspacePath, string dir)
        => Path.Combine(CommitDir(workspacePath, dir), "pack.compressed.xml");

    /// <summary>Short, filesystem-safe folder name for a commit (§12.18: keep it short, not 40 chars).</summary>
    public static string CommitDirName(string commit)
    {
        var trimmed = (commit ?? string.Empty).Trim();
        return trimmed.Length <= 12 ? trimmed : trimmed[..12];
    }

    public static RepoContextIndex? ReadCurrent(string workspacePath)
        => ReadJson<RepoContextIndex>(CurrentJsonPath(workspacePath));

    public static void WriteCurrent(string workspacePath, RepoContextIndex index)
    {
        var path = CurrentJsonPath(workspacePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(index, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static RepoContextMeta? ReadMeta(string workspacePath, string dir)
        => ReadJson<RepoContextMeta>(MetaPath(workspacePath, dir));

    public static void WriteMeta(string commitDirPath, RepoContextMeta meta)
    {
        Directory.CreateDirectory(commitDirPath);
        File.WriteAllText(Path.Combine(commitDirPath, "meta.json"), JsonSerializer.Serialize(meta, JsonOptions));
    }

    /// <summary>
    /// Appends <c>devteam/context/</c> to <c>.git/info/exclude</c> (a local-only ignore that never
    /// touches the user's <c>.gitignore</c> or their diffs — spec REQ-004). Idempotent; returns
    /// true when the file changed.
    /// </summary>
    public static bool EnsureGitExcluded(string workspacePath)
    {
        var gitDir = Path.Combine(workspacePath, ".git");
        // A worktree/submodule has .git as a file pointing elsewhere; we can't safely edit that
        // from here, so leave it alone.
        if (!Directory.Exists(gitDir))
            return false;

        var excludePath = Path.Combine(gitDir, "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(excludePath)!);

        var existing = File.Exists(excludePath) ? File.ReadAllText(excludePath) : string.Empty;
        if (existing.Split('\n').Any(line => line.Trim().Equals(ExcludeLine, StringComparison.Ordinal)))
            return false;

        var separator = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : "\n";
        File.AppendAllText(excludePath, $"{separator}{ExcludeLine}\n");
        return true;
    }

    /// <summary>
    /// Deletes every commit folder except the newest <paramref name="keepLast"/> (by
    /// <c>meta.json.builtAtUtc</c>, never folder mtime — §12.19) and the one
    /// <paramref name="currentDir"/> points at. Returns the folders removed.
    /// </summary>
    public static IReadOnlyList<string> Prune(string workspacePath, int keepLast, string? currentDir)
    {
        var root = ContextRoot(workspacePath);
        if (!Directory.Exists(root))
            return [];

        var folders = Directory.EnumerateDirectories(root)
            .Where(dir => !Path.GetFileName(dir).StartsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(dir => (Path: dir, Name: Path.GetFileName(dir), BuiltAt: ReadMeta(workspacePath, Path.GetFileName(dir))?.BuiltAtUtc ?? DateTimeOffset.MinValue))
            .OrderByDescending(entry => entry.BuiltAt)
            .ToList();

        var removed = new List<string>();
        for (var i = Math.Max(0, keepLast); i < folders.Count; i++)
        {
            var entry = folders[i];
            if (string.Equals(entry.Name, currentDir, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                Directory.Delete(entry.Path, recursive: true);
                removed.Add(entry.Name);
            }
            catch (IOException)
            {
                // A locked/partially-deleted folder is not worth failing a refresh over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // A corrupt pointer/stamp is treated as "no index" rather than crashing the caller
            // (spec REQ-006, "unreadable/corrupt → treat as none").
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
