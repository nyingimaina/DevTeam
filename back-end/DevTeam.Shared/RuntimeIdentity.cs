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

    private RuntimeIdentity(int port, string dataDirectory, string appHomeDirectory, string? openCodePath)
    {
        Port = port;
        DataDirectory = dataDirectory;
        AppHomeDirectory = appHomeDirectory;
        MutexName = "DevTeam.Desktop";
        OpenCodePath = openCodePath;
    }

    /// <summary>
    /// Resolves identity from command line args and environment variables.
    /// </summary>
    public static RuntimeIdentity Resolve(
        IReadOnlyList<string> args,
        string? portEnv,
        string? dataDirEnv,
        string userProfile,
        string localAppData)
    {
        var port = ParsePort(ReadArg(args, PortArgument) ?? portEnv, DefaultPort);
        var dataDirectory = ResolveDataDirectory(
            ReadArg(args, DataDirectoryArgument) ?? dataDirEnv,
            userProfile);
        var appHome = Path.Combine(localAppData, "DevTeam");

        return new RuntimeIdentity(port, dataDirectory, appHome, ResolveOpenCodePath(localAppData));
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

    private static string? ResolveOpenCodePath(string localAppData)
    {
        var candidates = new[]
        {
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "opencode.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "opencode", "opencode.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}