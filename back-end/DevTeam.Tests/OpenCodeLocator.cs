namespace DevTeam.Tests;

/// <summary>
/// Locates the <c>opencode</c> executable. Returns null when opencode is not
/// installed so tests that talk to the real agent can skip portably.
/// </summary>
internal static class OpenCodeLocator
{
    public static string? ResolvePath()
    {
        var knownPaths = new[]
        {
            Environment.GetEnvironmentVariable("OPENCODE_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "opencode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "opencode", "opencode.exe"),
        };

        var onDisk = knownPaths
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .FirstOrDefault();

        if (onDisk is not null)
            return onDisk;

        // Fall back to PATH lookup without launching the binary.
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim('"'), "opencode.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}