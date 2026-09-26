using Avalonia.Controls;
using DevTeam.Desktop.Services;

namespace DevTeam.Desktop.Tests;

internal sealed class FakeBrokerProcess : IBrokerProcess
{
    public int Id => 4242;

    public bool HasExited { get; set; }

    public bool Killed { get; private set; }

    public bool Disposed { get; private set; }

    public void Kill()
    {
        Killed = true;
        HasExited = true;
    }

    public bool WaitForExit(int milliseconds) => true;

    public void Dispose() => Disposed = true;
}

internal sealed class FakeBrokerLauncher : IBrokerLauncher
{
    public List<FakeBrokerProcess> Processes { get; } = new();

    public FakeBrokerProcess Process => Processes[^1];

    public int StartCount => Processes.Count;

    public string? ExecutablePath { get; private set; }

    public IReadOnlyList<string>? Arguments { get; private set; }

    public string? WorkingDirectory { get; private set; }

    public bool ThrowOnStart { get; set; }

    public IBrokerProcess Start(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        if (ThrowOnStart)
            throw new InvalidOperationException("boom");

        var process = new FakeBrokerProcess();
        Processes.Add(process);
        ExecutablePath = executablePath;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        return process;
    }
}

internal sealed class FakeHealthProbe : IHealthProbe
{
    public int Calls { get; private set; }

    /// <summary>Health starts answering true once the call count reaches this value.</summary>
    public int SucceedOnCall { get; set; } = 1;

    public Task<bool> IsHealthyAsync(Uri healthUrl, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Calls >= SucceedOnCall);
    }
}

internal sealed class FakeRegistryReader : IRegistryReader
{
    public string? MachineValue { get; set; }

    public string? UserValue { get; set; }

    /// <summary>Machine subkeys the reader was asked for, in order.</summary>
    public List<string> MachineSubKeys { get; } = [];

    /// <summary>User subkeys the reader was asked for, in order.</summary>
    public List<string> UserSubKeys { get; } = [];

    public string? ReadMachineString(string subKey, string valueName)
    {
        MachineSubKeys.Add(subKey);
        return MachineValue;
    }

    public string? ReadUserString(string subKey, string valueName)
    {
        UserSubKeys.Add(subKey);
        return UserValue;
    }
}

internal sealed class FakeOpenCodeProbe : IOpenCodeProbe
{
    public string? Path { get; set; }

    public bool DesktopAppInstalled { get; set; }

    public string? ResolvePath() => Path;

    public string? DescribeMissing() =>
        Path is not null
            ? null
            : DesktopAppInstalled
                ? OpenCodeProbe.DesktopAppOnlyMessage
                : OpenCodeProbe.MissingMessage;
}

internal sealed class FakeWebView2RuntimeProbe : IWebView2RuntimeProbe
{
    public bool Installed { get; set; }

    public bool IsInstalled() => Installed;

    public string? GetVersion() => Installed ? "120.0.0.0" : null;
}

/// <summary>
/// Stands in for the embedded web view so the behavioural probe can be exercised without a live
/// WebView2 runtime. Completes navigation on demand, or never, to model both outcomes.
/// </summary>
internal sealed class FakeWebViewNavigationController : IWebViewNavigationController
{
    /// <summary>When false, navigation never completes and the probe must time out.</summary>
    public bool CompletesNavigation { get; set; } = true;

    public bool NavigationSucceeds { get; set; } = true;

    /// <summary>Every URL the shell asked the view to display, in order.</summary>
    public List<Uri> NavigatedTo { get; } = [];

    public event EventHandler<WebViewNavigationCompletedEventArgs>? NavigationCompleted;

    public Uri? Source
    {
        set
        {
            if (value is not null)
            {
                NavigatedTo.Add(value);
                CompleteIfConfigured();
            }
        }
    }

    private void CompleteIfConfigured()
    {
        if (!CompletesNavigation)
            return;

        NavigationCompleted?.Invoke(this, new WebViewNavigationCompletedEventArgs
        {
            IsSuccess = NavigationSucceeds
        });
    }
}
