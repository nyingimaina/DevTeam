using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using DevTeam.Desktop.Services;

namespace DevTeam.Desktop;

public partial class App : Application
{
    private readonly ShellStartupCoordinator _coordinator;
    private readonly string _webViewProfileDirectory;

    public App(ShellStartupCoordinator coordinator, string webViewProfileDirectory)
    {
        _coordinator = coordinator;
        _webViewProfileDirectory = webViewProfileDirectory;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new MainWindow(_coordinator);
            mainWindow.Browser.EnvironmentRequested += OnWebViewEnvironmentRequested;
            desktop.MainWindow = mainWindow;
            desktop.ShutdownRequested += (_, _) => _coordinator.Shutdown();
            mainWindow.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// REQ-17: keep the WebView2 profile inside the shell's app home. The control exposes no
    /// user-data property and does not read the environment variable, so this request is the only
    /// place the location can be applied.
    /// </summary>
    private void OnWebViewEnvironmentRequested(object? sender, WebViewEnvironmentRequestedEventArgs e)
    {
        if (e is WindowsWebView2EnvironmentRequestedEventArgs windowsArgs)
            WebViewEnvironmentProfile.Apply(windowsArgs, _webViewProfileDirectory);
    }
}
