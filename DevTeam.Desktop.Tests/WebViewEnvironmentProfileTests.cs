using DevTeam.Desktop.Services;

namespace DevTeam.Desktop.Tests;

/// <summary>
/// REQ-17: the shell's app home owns the WebView2 profile. The environment variable that looked
/// like it did this is not read by the WebView control, so the folder has to be set on the
/// environment request the control raises.
/// </summary>
public sealed class WebViewEnvironmentProfileTests
{
    private const string AppHomeProfile = @"C:\Users\tester\AppData\Local\DevTeam\WebView2";

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_ProfileRequest_UsesTheAppHomeWhenNoneIsChosen()
    {
        var resolved = WebViewEnvironmentProfile.Resolve(currentUserDataFolder: null, AppHomeProfile);

        Assert.Equal(AppHomeProfile, resolved);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_ProfileRequest_LeavesAnAlreadyChosenProfileAlone()
    {
        var resolved = WebViewEnvironmentProfile.Resolve(@"C:\preexisting\profile", AppHomeProfile);

        Assert.Equal(@"C:\preexisting\profile", resolved);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_ProfileRequest_IgnoresBlankAppHomeProfile()
    {
        var resolved = WebViewEnvironmentProfile.Resolve(currentUserDataFolder: null, "   ");

        Assert.Null(resolved);
    }

    [Fact]
    [Trait("Requirement", "REQ-17")]
    public void REQ_17_ProfileRequest_TreatsWhitespaceAsUnchosen()
    {
        var resolved = WebViewEnvironmentProfile.Resolve("   ", AppHomeProfile);

        Assert.Equal(AppHomeProfile, resolved);
    }
}
