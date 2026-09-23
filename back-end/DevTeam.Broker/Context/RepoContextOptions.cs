namespace DevTeam.Broker.Context;

/// <summary>
/// The state of a workspace's code overview, in the terms the UI speaks (§9 of the spec).
/// Internal names never reach the user — the frontend maps these to plain sentences.
/// </summary>
public enum RepoContextState
{
    None,
    UpToDate,
    Behind,
    Refreshing,
    Unavailable,
}

/// <summary>A snapshot of the code overview for one workspace.</summary>
public sealed record RepoContextStatus(
    RepoContextState State,
    int? ChangesBehind,
    DateTimeOffset? BuiltAt,
    IReadOnlyList<string> Warnings);

/// <summary>Outcome of one refresh attempt. Never thrown — the service returns this instead.</summary>
public sealed record RepoContextRefreshResult(
    string Outcome,
    string? Commit,
    IReadOnlyList<string> Warnings)
{
    public static RepoContextRefreshResult Skipped(string reason) => new(reason, null, []);
}

/// <summary>
/// Configuration for the code-overview feature. Bound from the <c>RepoContext</c> section of
/// appsettings.json; every default here matches §8 of the spec.
/// </summary>
public sealed class RepoContextOptions
{
    public bool Enabled { get; set; } = true;

    public string RepomixCommand { get; set; } = "repomix";

    public int TimeoutSeconds { get; set; } = 120;

    public long MaxPackBytes { get; set; } = 25 * 1024 * 1024;

    public int KeepLast { get; set; } = 3;

    public List<string> ExtraIgnorePatterns { get; set; } = [];

    // The deterministic map writer is the default: it costs nothing and cannot hallucinate.
    // A model-written prose pass can be layered on later behind this flag (see REPOMIX_BRS §14).
    public bool GenerateMapWithModel { get; set; }

    public TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(1, TimeoutSeconds));

    // A single comma-separated pattern string for Repomix's --ignore. devteam/context/** MUST be
    // present or each pack would contain the previous pack and grow without bound (§12.6).
    public string BuildIgnoreArgument()
    {
        var patterns = new List<string>(DefaultIgnorePatterns);
        patterns.AddRange(SecretDenyList.Select(name => $"**/{name}"));
        patterns.AddRange(ExtraIgnorePatterns);
        return string.Join(',', patterns);
    }

    public static readonly string[] DefaultIgnorePatterns =
    [
        "**/node_modules/**",
        "**/bin/**",
        "**/obj/**",
        "**/.next/**",
        "**/out/**",
        "**/dist/**",
        "**/coverage/**",
        "**/*.lock",
        "**/package-lock.json",
        "**/*.db",
        "**/*.sqlite*",
        "**/*.log",
        "**/.env*",
        "**/*.pem",
        "**/*.pfx",
        "**/*.key",
        "devteam/context/**",
    ];

    // Files DevTeam refuses to hand to Repomix even before its own scan runs (§13, "apply
    // DevTeam's own deny-list before running Repomix"). Matched case-insensitively on the
    // file name or extension.
    public static readonly string[] SecretDenyList =
    [
        ".env", ".env.local", ".env.production",
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
        ".npmrc", ".pypirc", "credentials", "secrets.json",
    ];
}
