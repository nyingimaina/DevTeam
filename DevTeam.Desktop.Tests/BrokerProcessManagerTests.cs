using DevTeam.Desktop.Services;
using DevTeam.Shared;

namespace DevTeam.Desktop.Tests;

public sealed class BrokerProcessManagerTests : IDisposable
{
    private readonly string _brokerExe = Path.Combine(Path.GetTempPath(), $"DevTeam.Broker-{Guid.NewGuid():N}.exe");

    public BrokerProcessManagerTests() => File.WriteAllText(_brokerExe, "stub");

    public void Dispose()
    {
        if (File.Exists(_brokerExe))
            File.Delete(_brokerExe);
    }

    private static RuntimeIdentity Identity(string? dataDir = null) =>
        RuntimeIdentity.Resolve(
            dataDir is null ? [] : [$"--data-dir={dataDir}"],
            null,
            null,
            @"C:\Users\tester",
            @"C:\Users\tester\AppData\Local");

    private BrokerProcessManager Create(
        FakeBrokerLauncher launcher,
        FakeHealthProbe health,
        RuntimeIdentity? identity = null,
        string? exe = null) =>
        new(identity ?? Identity(), launcher, health, exe ?? _brokerExe,
            healthTimeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(1));

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_StartAsync_LaunchesBrokerAndWaitsForHealth()
    {
        var launcher = new FakeBrokerLauncher();
        var health = new FakeHealthProbe { SucceedOnCall = 2 };

        var result = await Create(launcher, health).StartAsync();

        Assert.True(result.Ok);
        Assert.Equal(1, launcher.StartCount);
        Assert.True(health.Calls >= 2);
    }

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_StartAsync_BrokerNeverHealthy_ReturnsPlainLanguageErrorWithRetry()
    {
        var launcher = new FakeBrokerLauncher();
        var health = new FakeHealthProbe { SucceedOnCall = int.MaxValue };

        var result = await Create(launcher, health).StartAsync();

        Assert.False(result.Ok);
        Assert.Equal(BrokerProcessManager.TimedOutMessage, result.Message);
        Assert.DoesNotContain("Exception", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_StartAsync_AfterFailure_CanSucceedOnRetry()
    {
        var launcher = new FakeBrokerLauncher();
        var health = new FakeHealthProbe { SucceedOnCall = int.MaxValue };
        var manager = Create(launcher, health);

        var first = await manager.StartAsync();
        Assert.False(first.Ok);

        health.SucceedOnCall = health.Calls + 1;
        var second = await manager.StartAsync();

        Assert.True(second.Ok);
        Assert.Equal(2, launcher.StartCount);
    }

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_StartAsync_MissingBrokerExecutable_ReturnsPlainMessage()
    {
        var launcher = new FakeBrokerLauncher();
        var health = new FakeHealthProbe();
        var manager = Create(launcher, health, exe: Path.Combine(Path.GetTempPath(), "does-not-exist.exe"));

        var result = await manager.StartAsync();

        Assert.False(result.Ok);
        Assert.Equal(BrokerProcessManager.MissingBrokerMessage, result.Message);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_StartAsync_LauncherThrows_ReturnsPlainMessage()
    {
        var launcher = new FakeBrokerLauncher { ThrowOnStart = true };
        var health = new FakeHealthProbe();

        var result = await Create(launcher, health).StartAsync();

        Assert.False(result.Ok);
        Assert.Equal(BrokerProcessManager.CouldNotStartMessage, result.Message);
        Assert.DoesNotContain("boom", result.Message);
    }

    [Fact]
    [Trait("Requirement", "REQ-6")]
    public async Task REQ_6_Shutdown_TerminatesSpawnedBroker()
    {
        var launcher = new FakeBrokerLauncher();
        var health = new FakeHealthProbe();
        var manager = Create(launcher, health);

        await manager.StartAsync();
        manager.Shutdown();

        Assert.True(launcher.Process.Killed);
        Assert.True(launcher.Process.Disposed);
    }

    [Fact]
    [Trait("Requirement", "REQ-6")]
    public async Task REQ_6_NoBrokerSpawnedUntilStartRequested()
    {
        var launcher = new FakeBrokerLauncher();
        _ = Create(launcher, new FakeHealthProbe());

        Assert.Equal(0, launcher.StartCount);

        await Task.CompletedTask;
    }

    [Fact]
    [Trait("Requirement", "REQ-6")]
    public async Task REQ_6_StartAsync_WhenAlreadyRunning_DoesNotSpawnSecondBroker()
    {
        var launcher = new FakeBrokerLauncher();
        var manager = Create(launcher, new FakeHealthProbe());

        await manager.StartAsync();
        await manager.StartAsync();

        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    [Trait("Requirement", "REQ-11")]
    public void REQ_11_UiUrl_LoadsRootWithNoPickerPath()
    {
        var manager = Create(new FakeBrokerLauncher(), new FakeHealthProbe());

        Assert.Equal("/", manager.UiUrl.AbsolutePath);
        Assert.Equal(string.Empty, manager.UiUrl.Query);
    }

    [Fact]
    [Trait("Requirement", "REQ-15")]
    public void REQ_15_HealthUrl_AndUiUrl_AreLoopbackOnly()
    {
        var manager = Create(new FakeBrokerLauncher(), new FakeHealthProbe());

        Assert.Equal("127.0.0.1", manager.UiUrl.Host);
        Assert.Equal("127.0.0.1", manager.HealthUrl.Host);
        Assert.Equal("/healthz", manager.HealthUrl.AbsolutePath);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public async Task REQ_17_StartAsync_UsesRuntimeIdentityPortAndDataDirectory()
    {
        var launcher = new FakeBrokerLauncher();
        var identity = RuntimeIdentity.Resolve(
            ["--port=5999", @"--data-dir=C:\work\devteam-data"],
            null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");
        var manager = Create(launcher, new FakeHealthProbe(), identity);

        await manager.StartAsync();

        Assert.Contains("--port=5999", launcher.Arguments!);
        Assert.Contains(@"--data-dir=C:\work\devteam-data", launcher.Arguments!);
        Assert.Equal(5999, manager.Port);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_ShellSource_ReusesRuntimeIdentity_WithNoDuplicatedPort()
    {
        var program = RepoPaths.ReadShellFile("Program.cs");
        var manager = RepoPaths.ReadShellFile("Services", "BrokerProcessManager.cs");

        Assert.Contains("RuntimeIdentity", program);
        Assert.Contains("RuntimeIdentity", manager);
        Assert.DoesNotContain("5202", manager);
        Assert.DoesNotContain("5202", program);
    }
}
