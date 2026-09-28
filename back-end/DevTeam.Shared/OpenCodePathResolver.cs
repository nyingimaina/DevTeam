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

    /// <summary>
    /// The path to actually hand to <c>Process.Start</c>: the link's final target when the entry is
    /// a reparse point, otherwise the path unchanged.
    /// </summary>
    /// <remarks>
    /// winget installs the CLI as a <em>symbolic link</em> in
    /// <c>%LOCALAPPDATA%\Microsoft\WinGet\Links</c> pointing into its versioned package folder, and
    /// Windows can refuse to create a process whose image path traverses a reparse point — the
    /// call fails with Win32Exception 448, "the path cannot be traversed because it contains an
    /// untrusted mount point", which is how the broker failed in the wild. Resolving the link
    /// first turns that path into the real executable underneath it, with no reparse point left
    /// to traverse. The refusal itself depends on the launching process's mitigation state and
    /// could not be reproduced from a test host, so this is a mitigation rather than a proven
    /// cure; the link resolution it performs is asserted directly.
    ///
    /// Discovery deliberately still reports the link: it is the path a user sees in
    /// <c>where opencode</c> and in PATH, so it is the right thing to log and to name in the
    /// missing-CLI message. Only the launch target is rewritten.
    /// </remarks>
    public static string LaunchTarget(string path) => ResolveLaunchTarget(path).Path;

    /// <summary>
    /// Every spelling of <paramref name="path"/> worth trying when starting the CLI, best first.
    ///
    /// <para>
    /// This exists because resolving the winget symbolic link is not enough on its own. When
    /// <see cref="ResolveLaunchTarget"/> succeeds the real binary is first and the link is kept as a
    /// fallback; when it <em>fails</em> — its own filesystem call can throw, and it used to fail
    /// silently — the link would be the only candidate, and the link is exactly what Windows had
    /// already refused with error 448. In the wild that collapsed the chain to one attempt and the
    /// chat died with a 503. So the versioned winget package folder is searched directly, which finds
    /// the real binary without consulting the link at all.
    /// </para>
    /// </summary>
    /// <param name="path">The path discovery settled on.</param>
    /// <param name="localAppData">
    /// Where winget keeps its packages. Defaults to this machine's; the parameter exists so the
    /// search can be pointed at a fixture instead of whatever is actually installed.
    /// </param>
    public static IReadOnlyList<string> LaunchCandidates(string path, string? localAppData = null)
    {
        var candidates = new List<string>();
        var resolved = ResolveLaunchTarget(path).Path;
        Add(resolved);
        Add(path);
        foreach (var packaged in WingetPackageBinaries(localAppData ?? DefaultLocalAppData(), path))
            Add(packaged);

        return candidates;

        void Add(string candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                !candidates.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                candidates.Add(candidate);
        }
    }

    /// <summary>
    /// The real binary inside a winget package folder, found without going through the link.
    /// Ordered by folder name descending so the newest package is offered first.
    /// </summary>
    private static IEnumerable<string> WingetPackageBinaries(string localAppData, string path)
    {
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
            yield break;

        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages))
            yield break;

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(packages, "*" + BaseName + "*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var folder in folders.OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(folder, fileName!);
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private static string DefaultLocalAppData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// The launch target plus, when the link could not be resolved, the reason it was left alone.
    /// A resolver that fails silently is indistinguishable from one that was never called — the
    /// broker hit exactly that, so the outcome is reported rather than swallowed.
    /// </summary>
    public static LaunchTargetResolution ResolveLaunchTarget(string path)
    {
        try
        {
            var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (target is null)
                return new(path, "not a reparse point");

            return string.IsNullOrWhiteSpace(target.FullName)
                ? new(path, "link has no target")
                : new(target.FullName, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(path, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Outcome of <see cref="ResolveLaunchTarget"/>: the path to launch, and why the original was
    /// kept when <see cref="UnresolvedReason"/> is set.
    /// </summary>
    public readonly record struct LaunchTargetResolution(string Path, string? UnresolvedReason)
    {
        /// <summary>True when <see cref="Path"/> is the link's final target, false when the original was kept.</summary>
        public bool Resolved => UnresolvedReason is null;
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
