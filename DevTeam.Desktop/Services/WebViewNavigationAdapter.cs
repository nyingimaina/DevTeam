using Avalonia.Controls;

namespace DevTeam.Desktop.Services;

/// <summary>Adapts the real embedded view to <see cref="IWebViewNavigationController"/>.</summary>
public sealed class WebViewNavigationAdapter : IWebViewNavigationController
{
    private readonly NativeWebView _webView;

    public WebViewNavigationAdapter(NativeWebView webView) => _webView = webView;

    public event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted
    {
        add => _webView.NavigationCompleted += value;
        remove => _webView.NavigationCompleted -= value;
    }

    public Uri? Source
    {
        set
        {
            if (value is not null)
                _webView.Source = value;
        }
    }
}
