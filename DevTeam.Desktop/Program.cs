using System.Threading;
using Avalonia;
using DevTeam.Desktop.Services;
using DevTeam.Shared;
using Serilog;

namespace DevTeam.Desktop;

internal static class Program
{
    private static Mutex? _mutex;

    [STAThread]
    private static void Main(string[] args)
    {
        // REQ-17: the shell reuses the shared runtime identity — one source for the port, the
        // data directory, the app home, and the single-instance mutex.
        var identity = RuntimeIdentity.Resolve(
            args,
            portEnv: Environment.GetEnvironmentVariable("DEVTEAM_PORT"),
            dataDirEnv: Environment.GetEnvironmentVariable("DEVTEAM_DATA_DIR"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        // REQ-6: on-demand only — a second launch just focuses nothing and exits, and nothing
        // registers the shell to start with Windows.
        _mutex = new Mutex(true, identity.MutexName, out var isNew);
        if (!isNew)
            return;

        Directory.CreateDirectory(identity.WebView2Directory);
        Directory.CreateDirectory(identity.DesktopLogDirectory);
        var webViewProfileDirectory = identity.WebView2Directory;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(identity.DesktopLogDirectory, "desktop-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal((Exception?)e.ExceptionObject, "Unhandled AppDomain exception");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Fatal(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        try
        {
            // Record how opencode was located so a "cannot see opencode" report is diagnosable
            // from the desktop log alone.
            if (identity.OpenCodePath is null)
            {
                Log.Warning(
                    "opencode CLI not found. Searched: {Directories}. Desktop app installed: {DesktopApp}",
                    string.Join(", ", identity.OpenCodeSearchedDirectories),
                    identity.OpenCodeDesktopAppInstalled);
            }
            else
            {
                Log.Information(
                    "opencode CLI resolved to {Path} (requires shell: {RequiresShell})",
                    identity.OpenCodePath,
                    identity.OpenCodeRequiresShell);
            }

            var broker = new BrokerProcessManager(
                identity,
                new ProcessBrokerLauncher(),
                new HttpHealthProbe());
            var coordinator = new ShellStartupCoordinator(
                broker,
                new OpenCodeProbe(identity),
                new WebView2RuntimeProbe(new WindowsRegistryReader()));

            BuildAvaloniaApp(coordinator, webViewProfileDirectory).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DevTeam Desktop failed to start");
        }
        finally
        {
            Log.CloseAndFlush();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }

    private static AppBuilder BuildAvaloniaApp(ShellStartupCoordinator coordinator, string webViewProfileDirectory)
    {
        return AppBuilder.Configure(() => new App(coordinator, webViewProfileDirectory))
            .UsePlatformDetect()
            .LogToTrace();
    }
}
