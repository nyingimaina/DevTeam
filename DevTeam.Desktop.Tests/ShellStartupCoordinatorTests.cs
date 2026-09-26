using DevTeam.Desktop.Services;
using DevTeam.Shared;

namespace DevTeam.Desktop.Tests;

public sealed class ShellStartupCoordinatorTests : IDisposable
{
    private readonly string _brokerExe = Path.Combine(Path.GetTempPath(), $"DevTeam.Broker-{Guid.NewGuid():N}.exe");

    public ShellStartupCoordinatorTests() => File.WriteAllText(_brokerExe, "stub");

    public void Dispose()
    {
        if (File.Exists(_brokerExe))
            File.Delete(_brokerExe);
    }

    private ShellStartupCoordinator Create(
        FakeHealthProbe health,
        FakeOpenCodeProbe openCode,
        FakeWebView2RuntimeProbe webView2)
    {
        var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", @"C:\Users\tester\AppData\Local");
        var broker = new BrokerProcessManager(
            identity, new FakeBrokerLauncher(), health, _brokerExe,
            healthTimeout: TimeSpan.FromSeconds(2), pollInterval: TimeSpan.FromMilliseconds(1));
        return new ShellStartupCoordinator(broker, openCode, webView2);
    }

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_Coordinator_BrokerReady_ExposesLoopbackUiUrl()
    {
        var state = await Create(
            new FakeHealthProbe(),
            new FakeOpenCodeProbe { Path = @"C:\tools\opencode.exe" },
            new FakeWebView2RuntimeProbe { Installed = true }).StartAsync();

        Assert.True(state.BrokerReady);
        Assert.Null(state.ErrorMessage);
        Assert.Equal("127.0.0.1", state.UiUrl.Host);
    }

    [Fact]
    [Trait("Requirement", "REQ-5")]
    public async Task REQ_5_Coordinator_BrokerFails_ExposesPlainErrorMessage()
    {
        var state = await Create(
            new FakeHealthProbe { SucceedOnCall = int.MaxValue },
            new FakeOpenCodeProbe { Path = @"C:\tools\opencode.exe" },
            new FakeWebView2RuntimeProbe { Installed = true }).StartAsync();

        Assert.False(state.BrokerReady);
        Assert.NotNull(state.ErrorMessage);
        Assert.DoesNotContain("Exception", state.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-14")]
    public async Task REQ_14_Coordinator_MissingOpenCode_ShowsPlainMessage()
    {
        var state = await Create(
            new FakeHealthProbe(),
            new FakeOpenCodeProbe { Path = null },
            new FakeWebView2RuntimeProbe { Installed = true }).StartAsync();

        Assert.Equal(OpenCodeProbe.MissingMessage, state.OpenCodeMessage);
        Assert.Contains("opencode", state.OpenCodeMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-14")]
    public async Task REQ_14_Coordinator_OpenCodePresent_ShowsNoWarning()
    {
        var state = await Create(
            new FakeHealthProbe(),
            new FakeOpenCodeProbe { Path = @"C:\tools\opencode.exe" },
            new FakeWebView2RuntimeProbe { Installed = true }).StartAsync();

        Assert.Null(state.OpenCodeMessage);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Coordinator_WebView2Missing_UsesBrowserFallback()
    {
        var state = await Create(
            new FakeHealthProbe(),
            new FakeOpenCodeProbe { Path = @"C:\tools\opencode.exe" },
            new FakeWebView2RuntimeProbe { Installed = false }).StartAsync();

        Assert.False(state.WebView2Available);
        Assert.True(state.UseBrowserFallback);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Coordinator_WebView2Present_NoBrowserFallback()
    {
        var state = await Create(
            new FakeHealthProbe(),
            new FakeOpenCodeProbe { Path = @"C:\tools\opencode.exe" },
            new FakeWebView2RuntimeProbe { Installed = true }).StartAsync();

        Assert.True(state.WebView2Available);
        Assert.False(state.UseBrowserFallback);
    }
}
