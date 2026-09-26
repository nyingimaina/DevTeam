using DevTeam.Desktop.Services;

namespace DevTeam.Desktop.Tests;

/// <summary>
/// REQ-4: the shell must prove the embedded view works by actually using it, not by reading a
/// registry string. A registry pre-flight can be wrong while the runtime is healthy, which is
/// exactly what sent the UI to an external browser instead of the app window.
/// </summary>
public sealed class WebView2ProbeTests
{
    private static readonly Uri UiUrl = new("http://127.0.0.1:5202/");

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Probe_NavigationSucceeds_ReportsWebViewAvailable()
    {
        var view = new FakeWebViewNavigationController { NavigationSucceeds = true };

        var available = await new WebView2Probe(view).ProbeAsync(Timeout, UiUrl);

        Assert.True(available);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Probe_NavigationFails_ReportsWebViewUnavailable()
    {
        var view = new FakeWebViewNavigationController { NavigationSucceeds = false };

        var available = await new WebView2Probe(view).ProbeAsync(Timeout, UiUrl);

        Assert.False(available);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Probe_NavigationNeverCompletes_TimesOutAndReportsUnavailable()
    {
        var view = new FakeWebViewNavigationController { CompletesNavigation = false };

        var available = await new WebView2Probe(view).ProbeAsync(Timeout, UiUrl);

        Assert.False(available);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Probe_AlwaysAttemptsNavigationBeforeDeciding()
    {
        var view = new FakeWebViewNavigationController();

        await new WebView2Probe(view).ProbeAsync(Timeout, UiUrl);

        Assert.Equal(new[] { UiUrl }, view.NavigatedTo);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public async Task REQ_4_Probe_DoesNotFallBackWhenTheViewIsHealthy()
    {
        var view = new FakeWebViewNavigationController { NavigationSucceeds = true };

        var available = await new WebView2Probe(view).ProbeAsync(Timeout, UiUrl);

        Assert.True(available);
        Assert.Single(view.NavigatedTo);
    }
}
