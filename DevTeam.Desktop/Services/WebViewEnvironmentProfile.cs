using Avalonia.Platform;

namespace DevTeam.Desktop.Services;

/// <summary>
/// Points the WebView2 runtime at the shell's app home. The profile has to be set on the
/// environment request the control raises, because there is no user-data property on the control
/// itself and the <c>WEBVIEW2_USER_DATA_FOLDER</c> environment variable is not read by it.
/// </summary>
// REQ-17: the WebView2 profile lives under the shared app home.
public static class WebViewEnvironmentProfile
{
    /// <summary>
    /// Chooses the profile directory to use, preferring a location already chosen by the control so
    /// an explicit host decision is never overridden.
    /// </summary>
    public static string? Resolve(string? currentUserDataFolder, string? profileDirectory) =>
        string.IsNullOrWhiteSpace(currentUserDataFolder) && !string.IsNullOrWhiteSpace(profileDirectory)
            ? profileDirectory
            : currentUserDataFolder;

    /// <summary>Applies <see cref="Resolve"/> to a live environment request.</summary>
    public static void Apply(WindowsWebView2EnvironmentRequestedEventArgs args, string? profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);

        args.UserDataFolder = Resolve(args.UserDataFolder, profileDirectory);
    }
}
