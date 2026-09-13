namespace DevTeam.Broker.Server;

/// <summary>
/// Read-mostly filesystem browsing for the PathBrowser UI: list roots, list a
/// directory, stat a path, and create a single directory. Deliberately read +
/// mkdir only; mutations of existing content live with the agent/hub, not here.
/// </summary>
public interface IFileSystemService
{
    IReadOnlyList<FileSystemRootDto> GetRoots();

    IReadOnlyList<FileSystemEntryDto> ListDirectory(string path);

    FileSystemStatDto GetStat(string path);

    FileSystemStatDto CreateDirectory(string path);
}

public sealed class FileSystemService : IFileSystemService
{
    public IReadOnlyList<FileSystemRootDto> GetRoots()
    {
        var roots = new List<FileSystemRootDto>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            string label;
            try
            {
                if (!drive.IsReady)
                    continue;
                label = drive.VolumeLabel;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var display = string.IsNullOrWhiteSpace(label)
                ? $"Drive ({drive.Name})"
                : $"{label} ({drive.Name})";
            roots.Add(new FileSystemRootDto(drive.RootDirectory.FullName, display));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
            roots.Add(new FileSystemRootDto(home, "Home"));

        return roots;
    }

    public IReadOnlyList<FileSystemEntryDto> ListDirectory(string path)
    {
        var full = NormalizeExistingDirectory(path);

        var entries = new List<FileSystemEntryDto>();
        foreach (var directory in EnumerateSafe(Directory.EnumerateDirectories(full)))
        {
            entries.Add(new FileSystemEntryDto(
                Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),
                directory,
                "directory",
                null));
        }

        foreach (var file in EnumerateSafe(Directory.EnumerateFiles(full)))
        {
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                size = 0;
            }
            catch (UnauthorizedAccessException)
            {
                size = 0;
            }

            entries.Add(new FileSystemEntryDto(
                Path.GetFileName(file),
                file,
                "file",
                size));
        }

        return entries
            .OrderBy(e => e.Kind == "directory" ? 0 : 1)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public FileSystemStatDto GetStat(string path)
    {
        var full = Normalize(path);

        var isDirectory = Directory.Exists(full);
        var isFile = File.Exists(full);
        var kind = isDirectory ? "directory" : isFile ? "file" : "missing";

        var isGitRepository = isDirectory &&
            (Directory.Exists(Path.Combine(full, ".git")) ||
             File.Exists(Path.Combine(full, ".git")));

        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        var name = string.IsNullOrEmpty(leaf) ? full : leaf;

        return new FileSystemStatDto(name, kind, isDirectory || isFile, isGitRepository);
    }

    public FileSystemStatDto CreateDirectory(string path)
    {
        var full = Normalize(path);
        if (Directory.Exists(full) || File.Exists(full))
            throw new InvalidOperationException($"Path already exists: {full}");

        Directory.CreateDirectory(full);
        return GetStat(full);
    }

    private static string NormalizeExistingDirectory(string path)
    {
        var full = Normalize(path);
        if (Directory.Exists(full) is false)
            throw new ArgumentException($"Directory does not exist: {full}");
        return full;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.");
        if (Path.IsPathRooted(path) is false)
            throw new ArgumentException("Path must be absolute.");
        return Path.GetFullPath(path);
    }

    private static IEnumerable<string> EnumerateSafe(IEnumerable<string> source)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (enumerator.MoveNext() is false)
                    yield break;
                current = enumerator.Current;
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            yield return current;
        }
    }
}