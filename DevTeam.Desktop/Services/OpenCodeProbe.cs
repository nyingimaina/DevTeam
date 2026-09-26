using DevTeam.Shared;

namespace DevTeam.Desktop.Services;

/// <summary>Resolves the opencode executable the shell depends on. Abstracted for tests.</summary>
public interface IOpenCodeProbe
{
    string? ResolvePath();

    /// <summary>
    /// The message to show when the CLI is missing, or null when it was found. Kept on the
    /// interface so the coordinator does not have to know how detection works.
    /// </summary>
    string? DescribeMissing();
}

/// <summary>
/// Resolves opencode through <see cref="RuntimeIdentity"/> — the shell never bundles or downloads
/// it. When it is missing the shell shows a plain-language explanation.
/// </summary>
// REQ-14: detect opencode (via RuntimeIdentity) and explain when it is missing; never bundle it.
// REQ-17: reuses RuntimeIdentity rather than reimplementing path resolution.
public sealed class OpenCodeProbe : IOpenCodeProbe
{
    private readonly RuntimeIdentity _identity;

    public OpenCodeProbe(RuntimeIdentity identity) => _identity = identity;

    public string? ResolvePath() => _identity.OpenCodePath;

    // REQ-14: a machine with only the OpenCode desktop app has opencode, but not the CLI DevTeam
    // drives, so the message explains that gap instead of repeating "install opencode". The
    // directories that were searched are included because "it is right there in my terminal" is
    // the common report, and the list turns that into something actionable.
    public string? DescribeMissing()
    {
        if (_identity.OpenCodePath is not null)
            return null;

        var message = _identity.OpenCodeDesktopAppInstalled ? DesktopAppOnlyMessage : MissingMessage;
        return _identity.OpenCodeSearchedDirectories.Count == 0
            ? message
            : message + " Looked for it in: " + string.Join("; ", _identity.OpenCodeSearchedDirectories) + ".";
    }

    /// <summary>Plain-language guidance shown when opencode cannot be found.</summary>
    public const string MissingMessage =
        "DevTeam needs the opencode command-line tool to run. " +
        "Install it with \"npm install -g opencode-ai\" (or \"winget install opencode\"), " +
        "then restart DevTeam." + OverrideHint;

    /// <summary>Guidance for the case where the desktop app is installed but the CLI is not.</summary>
    public const string DesktopAppOnlyMessage =
        "DevTeam needs the opencode command-line tool, which is separate from the OpenCode desktop app. " +
        "The desktop app you have installed does not include it. " +
        "Install the command-line tool with \"npm install -g opencode-ai\" (or \"winget install opencode\"), " +
        "then restart DevTeam." + OverrideHint;

    private const string OverrideHint =
        " If opencode works in a terminal, run \"where opencode\" and set the OPENCODE_PATH " +
        "environment variable to the path it prints.";
}
