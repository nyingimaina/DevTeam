namespace DevTeam.Desktop.Tests;

/// <summary>
/// REQ-4: guards the wiring that made the installed app open its UI in an external browser. The
/// embedded view must always be navigated, and the browser must only be a fallback for a genuine
/// navigation failure.
/// </summary>
public sealed class ShellWebViewWiringTests
{
    private static string MainWindowSource => RepoPaths.ReadShellFile("MainWindow.axaml.cs");

    private static string ProgramSource => RepoPaths.ReadShellFile("Program.cs");

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_Shell_AlwaysAttemptsTheEmbeddedViewBeforeAnyBrowserFallback()
    {
        var source = MainWindowSource;

        Assert.Contains("_webViewProbe.ProbeAsync", source, StringComparison.Ordinal);
        Assert.Contains("if (!embedded)", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_Shell_DoesNotDecideUsingTheRegistryVerdict()
    {
        var source = MainWindowSource;

        Assert.DoesNotContain("state.WebView2Available", source, StringComparison.Ordinal);
        Assert.DoesNotContain("state.UseBrowserFallback", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_Shell_LogsWhichPathItTook()
    {
        var source = MainWindowSource;

        Assert.Contains("Log.Information", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_Shell_UsesTheAppHomeWebViewProfile()
    {
        var appSource = RepoPaths.ReadShellFile("App.axaml.cs");

        Assert.Contains("WindowsWebView2EnvironmentRequestedEventArgs", appSource, StringComparison.Ordinal);
        Assert.Contains("EnvironmentRequested", appSource, StringComparison.Ordinal);
        Assert.Contains("WebViewEnvironmentProfile.Apply", appSource, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_Shell_ResolvesTheProfileFromTheSharedAppHome()
    {
        Assert.Contains("identity.WebView2Directory", ProgramSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The environment variable is not read by the WebView control, so leaving it in place would
    /// imply isolation the app does not actually have.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_Shell_DropsTheUnusedUserDataFolderEnvironmentVariable()
    {
        Assert.DoesNotContain("WEBVIEW2_USER_DATA_FOLDER", ProgramSource, StringComparison.Ordinal);
    }
}
