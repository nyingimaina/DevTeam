using Avalonia.Controls;

namespace DevTeam.Desktop.Services;

/// <summary>
/// Proves the embedded view works by using it: navigation is always attempted and the answer comes
/// from the navigation completing, not from a pre-flight check. A registry lookup cannot tell us
/// whether the runtime can actually render, and on x64 it does not even report the Evergreen
/// runtime that the 32-bit EdgeUpdate agent installs, which is what made the shell hand the UI to
/// an external browser on a machine where the runtime was present and healthy.
/// </summary>
// REQ-4: the shell attempts the in-app view and only falls back to the browser when navigation fails.
public sealed class WebView2Probe : IWebView2Probe
{
    private readonly IWebViewNavigationController _webView;

    public WebView2Probe(NativeWebView webView)
        : this(new WebViewNavigationAdapter(webView))
    {
    }

    public WebView2Probe(IWebViewNavigationController webView) => _webView = webView;

    public async Task<bool> ProbeAsync(TimeSpan timeout, Uri targetUrl)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<WebViewNavigationCompletedEventArgs> handler = (_, args) =>
            completion.TrySetResult(args.IsSuccess);

        _webView.NavigationCompleted += handler;
        _webView.Source = targetUrl;

        try
        {
            return await completion.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _webView.NavigationCompleted -= handler;
        }
    }
}
