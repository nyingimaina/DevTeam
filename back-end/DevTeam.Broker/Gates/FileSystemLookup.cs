namespace DevTeam.Broker.Gates;

// Resolves a file/directory name inside a directory tolerant of filesystem case-sensitivity
// differences (Windows/macOS default to case-insensitive; Linux ext4 is case-sensitive) — so
// a pipeline authored/tested on one OS behaves identically on another, e.g. CI running Linux
// against a release.yaml written on Windows.
public static class FileSystemLookup
{
    public static string? FindEntry(string directory, string name)
    {
        if (!Directory.Exists(directory))
            return null;

        // Always resolve via a case-insensitive scan and return the real on-disk path —
        // deliberately not a File.Exists(Path.Combine(...)) fast path: on a case-insensitive
        // *but case-preserving* filesystem (Windows, default macOS), that check can return
        // true for a differently-cased name while still handing back the requested (wrong)
        // casing instead of what's actually stored, which defeats the point of this utility.
        return Directory.EnumerateFileSystemEntries(directory)
            .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
    }
}
