namespace DevTeam.Shared;

/// <summary>
/// Resolves the runtime identity and isolation settings for a DevTeam broker
/// installation: TCP port, data directory, app home, and single-instance mutex.
/// Command line args win over environment variables, which win over defaults.
/// </summary>
public sealed record RuntimeIdentity
{
    public const int DefaultPort = 5202;

    private const string DataDirectoryArgument = "--data-dir";
    private const string PortArgument = "--port";

    /// <summary>TCP port the broker binds; the UI connects to it.</summary>
    public int Port { get; }

    /// <summary>Absolute path to the user data directory (SQLite DB, audit log).</summary>
    public string DataDirectory { get; }

    /// <summary>Absolute path to the per-user app home (WebView2 profile, desktop logs).</summary>
    public string AppHomeDirectory { get; }

    /// <summary>Named mutex guarding single-instance for the desktop process.</summary>
    public string MutexName { get; }

    /// <summary>WebView2 user-data folder.</summary>
    public string WebView2Directory => Path.Combine(AppHomeDirectory, "WebView2");

    /// <summary>Desktop rolling log directory.</summary>
    public string DesktopLogDirectory => Path.Combine(AppHomeDirectory, "logs");

    /// <summary>Broker SQLite database file.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "devteam.db");

    /// <summary>Broker rolling log directory (structured Serilog output, not the desktop shell's own log).</summary>
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Absolute path to the opencode executable, or null if not found.</summary>
    public string? OpenCodePath { get; }

    /// <summary>
    /// True when the resolved executable is a <c>.cmd</c>/<c>.bat</c> shim (npm, scoop, choco) and
    /// therefore has to be launched through <c>cmd.exe</c> rather than directly.
    /// </summary>
    public bool OpenCodeRequiresShell { get; }

    /// <summary>
    /// The directories that were searched for opencode. Kept so a failed detection can be explained
    /// and reported in the diagnostics bundle instead of leaving the user to guess.
    /// </summary>
    public IReadOnlyList<string> OpenCodeSearchedDirectories { get; }

    /// <summary>
    /// True when the OpenCode desktop app is installed. It ships no CLI, so this distinguishes
    /// "opencode is not installed at all" from "only the desktop app is installed".
    /// </summary>
    public bool OpenCodeDesktopAppInstalled { get; }

    private RuntimeIdentity(
        int port,
        string dataDirectory,
        string appHomeDirectory,
        OpenCodeResolution openCode,
        bool openCodeDesktopAppInstalled)
    {
        Port = port;
        DataDirectory = dataDirectory;
        AppHomeDirectory = appHomeDirectory;
        MutexName = "DevTeam.Desktop";
        OpenCodePath = openCode.Path;
        OpenCodeRequiresShell = openCode.RequiresShell;
        OpenCodeSearchedDirectories = openCode.SearchedDirectories;
        OpenCodeDesktopAppInstalled = openCodeDesktopAppInstalled;
    }

    /// <summary>
    /// Resolves identity from command line args and environment variables. <paramref name="pathEnv"/>
    /// and <paramref name="openCodePathEnv"/> default to the real environment, so tests can inject
    /// them and stay deterministic.
    /// </summary>
    public static RuntimeIdentity Resolve(
        IReadOnlyList<string> args,
        string? portEnv,
        string? dataDirEnv,
        string userProfile,
        string localAppData,
        string? pathEnv = null,
        string? openCodePathEnv = null)
    {
        var port = ParsePort(ReadArg(args, PortArgument) ?? portEnv, DefaultPort);
        var dataDirectory = ResolveDataDirectory(
            ReadArg(args, DataDirectoryArgument) ?? dataDirEnv,
            userProfile);
        var appHome = Path.Combine(localAppData, "DevTeam");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        // Both the override and PATH fall back to the real environment. The persisted PATH is
        // merged in because a Start-Menu launch carries whatever Explorer saw at logon, which
        // predates a CLI installed since.
        var explicitPath = openCodePathEnv ?? Environment.GetEnvironmentVariable("OPENCODE_PATH");
        var effectivePath = pathEnv ?? WindowsEnvironmentPath.Effective(Environment.GetEnvironmentVariable("PATH"));

        var openCode = OpenCodePathResolver.Probe(localAppData, effectivePath, explicitPath, programFiles);

        return new RuntimeIdentity(
            port,
            dataDirectory,
            appHome,
            openCode,
            OpenCodePathResolver.IsDesktopAppInstalled(localAppData));
    }

    public static string? ReadArg(IReadOnlyList<string> args, string name)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..].Trim();
        }

        return null;
    }

    private static int ParsePort(string? explicitPort, int fallback)
    {
        if (int.TryParse(explicitPort, out var port) && port is > 0 and <= 65535)
            return port;

        return fallback;
    }

    private static string ResolveDataDirectory(string? explicitDataDir, string userProfile)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataDir))
            return Path.GetFullPath(explicitDataDir);

        return Path.Combine(userProfile, ".devteam");
    }
}