namespace DevTeam.Shared;

/// <summary>
/// Builds the PATH a process should search for command-line tools.
/// </summary>
/// <remarks>
/// A DevTeam started from the Start Menu inherits the environment Explorer captured at logon. A
/// CLI installed after that is invisible to it even though a freshly opened terminal finds it,
/// because the terminal is started from the same stale snapshot. Re-reading the persisted machine
/// and user PATH and merging it with the process PATH closes that gap.
/// </remarks>
public static class WindowsEnvironmentPath
{
    /// <summary>
    /// The process PATH combined with the persisted machine and user PATH. Outside Windows the
    /// process PATH is returned untouched, since there is no registry to consult.
    /// </summary>
    public static string? Effective(string? processPath)
    {
        if (!OperatingSystem.IsWindows())
            return processPath;

        var machinePath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine);
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User);

        return Compose(machinePath, userPath, processPath);
    }

    /// <summary>
    /// Merges the three PATH sources, keeping the process entries first (they are the most specific)
    /// and dropping duplicates. Quoted entries and <c>%VARIABLE%</c> references are normalised.
    /// </summary>
    public static string? Compose(string? machinePath, string? userPath, string? processPath)
    {
        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddEntries(processPath, entries, seen);
        AddEntries(machinePath, entries, seen);
        AddEntries(userPath, entries, seen);

        return entries.Count == 0 ? null : string.Join(';', entries);
    }

    private static void AddEntries(string? path, List<string> entries, HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            if (seen.Add(entry))
                entries.Add(entry);
        }
    }
}
