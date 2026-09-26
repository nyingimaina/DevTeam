using Avalonia.Controls;

namespace DevTeam.Desktop.Services;

/// <summary>
/// The narrow slice of the embedded web view the startup probe needs. Exists so the decision can be
/// unit tested without a live WebView2 runtime.
/// </summary>
public interface IWebViewNavigationController
{
    event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted;

    Uri? Source { set; }
}
