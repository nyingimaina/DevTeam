using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DevTeam.Desktop.Services;
using Serilog;

namespace DevTeam.Desktop;

public partial class MainWindow : Window
{
    private static readonly TimeSpan WebViewNavigationTimeout = TimeSpan.FromSeconds(20);

    private readonly ShellStartupCoordinator _coordinator;
    private readonly IWebView2Probe _webViewProbe;
    private bool _startupDone;

    public MainWindow(ShellStartupCoordinator coordinator, IWebView2Probe? webViewProbe = null)
    {
        _coordinator = coordinator;
        InitializeComponent();
        _webViewProbe = webViewProbe ?? new WebView2Probe(Browser);
        Opened += async (_, _) => await RunStartupAsync();
    }

    private async Task RunStartupAsync()
    {
        SetStatus("Starting DevTeam...", showProgress: true, showRetry: false);
        OpenCodeBanner.IsVisible = false;
        RetryButton.IsVisible = false;

        try
        {
            // REQ-5: spawn the broker and wait for health before showing the UI.
            var state = await _coordinator.StartAsync();

            // REQ-14: plain-language opencode warning when it is missing.
            if (state.OpenCodeMessage is not null)
            {
                OpenCodeText.Text = state.OpenCodeMessage;
                OpenCodeBanner.IsVisible = true;
            }

            if (!state.BrokerReady)
            {
                // REQ-5 / REQ-18: a plain sentence with a clear next action, never a stack trace.
                Log.Warning("Broker did not become ready: {Message}", state.ErrorMessage);
                SetStatus(state.ErrorMessage ?? "DevTeam couldn't start. Please try again.",
                    showProgress: false, showRetry: true);
                return;
            }

            SetStatus("Opening DevTeam...", showProgress: true, showRetry: false);

            // REQ-4: try the in-app view first and only treat the browser as a fallback for a
            // genuine navigation failure. Deciding up front from a registry lookup reported a
            // healthy runtime as missing and sent the UI to the browser.
            // REQ-11: straight into the web UI, no shell-side picker.
            var embedded = await _webViewProbe.ProbeAsync(WebViewNavigationTimeout, state.UiUrl);

            if (!embedded)
            {
                Log.Warning("In-app view could not load {Url}; falling back to the default browser", state.UiUrl);
                OpenInBrowser(state.UiUrl);
                SetStatus("Opened DevTeam in your web browser.", showProgress: false, showRetry: false);
            }
            else
            {
                Log.Information("In-app view loaded {Url}", state.UiUrl);
                SetStatus("DevTeam is ready.", showProgress: false, showRetry: false);
            }

            _startupDone = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Startup failed");
            SetStatus("DevTeam couldn't start. Please try again.", showProgress: false, showRetry: true);
        }
    }

    private void SetStatus(string text, bool showProgress, bool showRetry)
    {
        StatusText.Text = text;
        StatusProgress.IsVisible = showProgress;
        RetryButton.IsVisible = showRetry;
    }

    private static void OpenInBrowser(Uri url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to open the default browser");
        }
    }

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        _startupDone = false;
        await RunStartupAsync();
    }

    private void OnAboutClick(object? sender, RoutedEventArgs e) => ShowAboutDialog();

    private async void OnExitClick(object? sender, RoutedEventArgs e)
    {
        await CloseSafelyAsync();
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_startupDone && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            await CloseSafelyAsync();
            Close();
            return;
        }

        _coordinator.Shutdown();
    }

    private Task CloseSafelyAsync()
    {
        try
        {
            _coordinator.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Shutdown failed");
        }

        return Task.CompletedTask;
    }

    private static string GetVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    private void ShowAboutDialog()
    {
        var dialog = new Window
        {
            Title = "About DevTeam",
            Width = 420,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var stack = new StackPanel { Spacing = 8, Margin = new Avalonia.Thickness(24) };
        stack.Children.Add(new TextBlock { Text = "DevTeam", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold });
        stack.Children.Add(new TextBlock { Text = $"Version {GetVersion()}" });
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Width = 80
        };
        close.Click += (_, _) => dialog.Close();
        stack.Children.Add(close);
        dialog.Content = stack;
        dialog.ShowDialog(this);
    }
}
