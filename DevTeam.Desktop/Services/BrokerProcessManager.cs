using DevTeam.Shared;

namespace DevTeam.Desktop.Services;

/// <summary>Outcome of trying to bring the broker up.</summary>
public sealed record BrokerStartResult(bool Ok, string Message)
{
    public static BrokerStartResult Ready() => new(true, string.Empty);

    public static BrokerStartResult Failed(string message) => new(false, message);
}

/// <summary>
/// Owns the broker child process: spawns it with the port and data directory resolved by
/// <see cref="RuntimeIdentity"/>, waits for <c>/healthz</c> to answer, and terminates the child
/// when the shell exits. The broker is loopback-only and never auto-started.
/// </summary>
// REQ-5: spawns the broker and waits for /healthz before the UI is shown.
// REQ-6: on-demand only; terminates the child it spawned when the shell exits.
// REQ-15: connects over loopback only.
// REQ-17: reuses RuntimeIdentity for the port and data directory.
public sealed class BrokerProcessManager : IDisposable
{
    private const string BrokerExecutableName = "DevTeam.Broker.exe";
    private const string HealthPath = "/healthz";

    private readonly RuntimeIdentity _identity;
    private readonly IBrokerLauncher _launcher;
    private readonly IHealthProbe _health;
    private readonly string _brokerExecutablePath;
    private readonly TimeSpan _healthTimeout;
    private readonly TimeSpan _pollInterval;
    private IBrokerProcess? _broker;
    private bool _disposed;

    public int Port => _identity.Port;

    public bool IsRunning => _broker is { HasExited: false };

    public BrokerProcessManager(
        RuntimeIdentity identity,
        IBrokerLauncher launcher,
        IHealthProbe health,
        string? brokerExecutablePath = null,
        TimeSpan? healthTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _identity = identity;
        _launcher = launcher;
        _health = health;
        _brokerExecutablePath = brokerExecutablePath ?? LocateBrokerBinary(AppContext.BaseDirectory) ?? string.Empty;
        _healthTimeout = healthTimeout ?? TimeSpan.FromSeconds(90);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <summary>The loopback URL the shell shows the web UI from.</summary>
    public Uri UiUrl => new($"http://127.0.0.1:{_identity.Port}/");

    public Uri HealthUrl => new($"http://127.0.0.1:{_identity.Port}{HealthPath}");

    /// <summary>
    /// Spawns the broker (if not already running) and waits for the health endpoint. Returns a
    /// plain-language failure instead of throwing so the shell can offer a Retry action (REQ-5).
    /// </summary>
    public async Task<BrokerStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning)
            return BrokerStartResult.Ready();

        if (string.IsNullOrEmpty(_brokerExecutablePath) || !File.Exists(_brokerExecutablePath))
            return BrokerStartResult.Failed(MissingBrokerMessage);

        var arguments = new[]
        {
            $"--port={_identity.Port}",
            $"--data-dir={_identity.DataDirectory}"
        };

        try
        {
            _broker = _launcher.Start(_brokerExecutablePath, arguments, Path.GetDirectoryName(_brokerExecutablePath)!);
        }
        catch (Exception)
        {
            return BrokerStartResult.Failed(CouldNotStartMessage);
        }

        if (await WaitForHealthyAsync(cancellationToken))
            return BrokerStartResult.Ready();

        StopBroker();
        return BrokerStartResult.Failed(TimedOutMessage);
    }

    /// <summary>Terminates the broker child this shell spawned (REQ-6).</summary>
    public void Shutdown()
    {
        StopBroker();
    }

    private async Task<bool> WaitForHealthyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _healthTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_broker is { HasExited: true })
                return false;

            if (await _health.IsHealthyAsync(HealthUrl, cancellationToken))
                return true;

            await Task.Delay(_pollInterval, cancellationToken);
        }

        return false;
    }

    private void StopBroker()
    {
        var broker = _broker;
        _broker = null;
        if (broker is null)
            return;

        try
        {
            if (!broker.HasExited)
            {
                broker.Kill();
                broker.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // The child is already gone; nothing left to do.
        }
        finally
        {
            broker.Dispose();
        }
    }

    /// <summary>Finds <c>DevTeam.Broker.exe</c> next to the shell.</summary>
    public static string? LocateBrokerBinary(string baseDirectory)
    {
        var candidate = Path.Combine(baseDirectory, BrokerExecutableName);
        return File.Exists(candidate) ? candidate : null;
    }

    internal const string MissingBrokerMessage =
        "DevTeam couldn't find its background service. Reinstall DevTeam and try again.";

    internal const string CouldNotStartMessage =
        "DevTeam couldn't start its background service. Close the app, then open it again.";

    internal const string TimedOutMessage =
        "DevTeam is taking longer than expected to start. Close the app, then open it again.";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopBroker();
        (_health as IDisposable)?.Dispose();
        (_launcher as IDisposable)?.Dispose();
    }
}
