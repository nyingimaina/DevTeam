namespace DevTeam.Desktop.Services;

/// <summary>Everything the shell window needs to render after a startup attempt.</summary>
public sealed record ShellStartupState
{
    public bool BrokerReady { get; init; }

    public required Uri UiUrl { get; init; }

    /// <summary>Plain-language broker failure message, or null when the broker is ready.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Plain-language opencode warning, or null when opencode is present.</summary>
    public string? OpenCodeMessage { get; init; }

    public bool WebView2Available { get; init; }

    /// <summary>True when WebView2 is missing and the UI must open in the default browser.</summary>
    public bool UseBrowserFallback => !WebView2Available;
}

/// <summary>
/// The shell's startup flow: detect the WebView2 runtime, resolve opencode, spawn the broker and
/// wait for health, then load the existing web UI directly — no workspace picker of its own.
/// </summary>
// REQ-5: orchestrates broker spawn + health wait, surfacing a plain-language error for Retry.
// REQ-11: loads the web UI as-is (root URL); the shell has no workspace/folder picker.
// REQ-14: surfaces the opencode warning without blocking the UI.
public sealed class ShellStartupCoordinator
{
    private readonly BrokerProcessManager _broker;
    private readonly IOpenCodeProbe _openCode;
    private readonly IWebView2RuntimeProbe _webView2;

    public ShellStartupCoordinator(
        BrokerProcessManager broker,
        IOpenCodeProbe openCode,
        IWebView2RuntimeProbe webView2)
    {
        _broker = broker;
        _openCode = openCode;
        _webView2 = webView2;
    }

    public async Task<ShellStartupState> StartAsync(CancellationToken cancellationToken = default)
    {
        var webView2Available = _webView2.IsInstalled();
        var openCodeMessage = _openCode.DescribeMissing();

        var broker = await _broker.StartAsync(cancellationToken);

        return new ShellStartupState
        {
            BrokerReady = broker.Ok,
            UiUrl = _broker.UiUrl,
            ErrorMessage = broker.Ok ? null : broker.Message,
            OpenCodeMessage = openCodeMessage,
            WebView2Available = webView2Available
        };
    }

    /// <summary>Stops the broker the shell spawned (called as the window closes).</summary>
    public void Shutdown() => _broker.Shutdown();
}
