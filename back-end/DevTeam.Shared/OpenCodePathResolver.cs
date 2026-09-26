namespace DevTeam.Shared;

/// <summary>
/// The outcome of looking for the opencode CLI: the executable when one was found, whether it
/// needs a shell to run, and the directories that were searched so a failure can be explained.
/// </summary>
public sealed record OpenCodeResolution(
    string? Path,
    bool RequiresShell,
    IReadOnlyList<string> SearchedDirectories);

/// <summary>
/// Locates the <c>opencode</c> command-line executable.
/// </summary>
/// <remarks>
/// The CLI is installed by winget, scoop, choco, npm, mise and the install script, and each of those
/// puts a differently-named entry in a different directory. npm in particular installs
/// <c>opencode.cmd</c> (the bin target is an .exe inside the package), and winget installs
/// <c>opencode.exe</c>. A shell finds all of them through PATHEXT, so probing only for
/// <c>opencode.exe</c> reports a perfectly good install as missing — which is exactly the bug this
/// resolver exists to avoid.
///
/// The OpenCode <em>desktop</em> app is a different product that ships no CLI and cannot answer
/// <c>opencode acp</c>, so it is never treated as a match. Windows matches file names
/// case-insensitively, so its directory is skipped outright and it is only detected so the
/// missing-CLI message can explain the difference.
/// </remarks>
public static class OpenCodePathResolver
{
    private const string BaseName = "opencode";
    private const string DesktopExecutableName = "OpenCode.exe";

    /// <summary>
    /// The suffixes probed, in the order a shell would try them. Restricted to what can be launched
    /// as a process: an extensionless npm shim is a shell script and is deliberately excluded.
    /// </summary>
    public static IReadOnlyList<string> CandidateExtensions { get; } = [".exe", ".cmd", ".bat", ".com"];

    /// <summary>
    /// Returns the opencode CLI, or null when it is not installed. An explicit path is honoured
    /// first, then the conventional install locations, then PATH.
    /// </summary>
    public static string? Resolve(string localAppData, string? pathEnv, string? explicitPath, string programFiles) =>
        Probe(localAppData, pathEnv, explicitPath, programFiles).Path;

    public static OpenCodeResolution Probe(string localAppData, string? pathEnv, string? explicitPath, string programFiles)
    {
        var searched = new List<string>();

        if (IsUsable(explicitPath))
            return Found(explicitPath, searched);

        var desktopDirectory = Path.GetDirectoryName(DesktopInstallLocation(localAppData));

        foreach (var directory in KnownInstallDirectories(localAppData, programFiles))
        {
            var match = FirstMatch(directory, searched);
            if (match is not null)
                return Found(match, searched);
        }

        foreach (var directory in SplitPath(pathEnv))
        {
            if (IsSameDirectory(directory, desktopDirectory))
                continue;

            var match = FirstMatch(directory, searched);
            if (match is not null)
                return Found(match, searched);
        }

        return new OpenCodeResolution(null, false, searched);
    }

    /// <summary>True when the path points at a <c>.cmd</c>/<c>.bat</c> shim that needs <c>cmd.exe</c>.</summary>
    public static bool RequiresShell(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the OpenCode desktop app is installed, regardless of whether a CLI exists.</summary>
    public static bool IsDesktopAppInstalled(string localAppData) =>
        File.Exists(DesktopInstallLocation(localAppData));

    public static string DesktopInstallLocation(string localAppData) =>
        Path.Combine(localAppData, "Programs", "opencode", DesktopExecutableName);

    /// <summary>
    /// The directories searched before PATH, in priority order. The OpenCode desktop app's folder is
    /// deliberately absent: Windows matches names case-insensitively, so listing it would hand the
    /// Electron GUI binary to a caller that is about to run <c>opencode acp</c> against it.
    /// </summary>
    public static IReadOnlyList<string> KnownInstallDirectories(string localAppData, string programFiles) =>
    [
        Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
        Path.Combine(programFiles, "opencode"),
    ];

    private static OpenCodeResolution Found(string path, IReadOnlyList<string> searched) =>
        new(path, RequiresShell(path), searched);

    private static string? FirstMatch(string directory, List<string> searched)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var trimmed = directory.Trim().Trim('"');
        searched.Add(trimmed);

        foreach (var extension in CandidateExtensions)
        {
            var candidate = Path.Combine(trimmed, BaseName + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> SplitPath(string? pathEnv) =>
        string.IsNullOrWhiteSpace(pathEnv)
            ? []
            : pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsSameDirectory(string directory, string? other) =>
        !string.IsNullOrWhiteSpace(other)
        && string.Equals(directory.Trim().Trim('"').TrimEnd('\\'), other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
