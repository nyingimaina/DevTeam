using DevTeam.Desktop.Services;
using DevTeam.Shared;

namespace DevTeam.Desktop.Tests;

public sealed class WebView2RuntimeProbeTests
{
    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_WebView2Probe_MissingKey_NotInstalled()
    {
        var probe = new WebView2RuntimeProbe(new FakeRegistryReader());

        Assert.False(probe.IsInstalled());
        Assert.Null(probe.GetVersion());
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_WebView2Probe_MachineKeyPresent_InstalledWithVersion()
    {
        var probe = new WebView2RuntimeProbe(new FakeRegistryReader { MachineValue = "120.0.2210.91" });

        Assert.True(probe.IsInstalled());
        Assert.Equal("120.0.2210.91", probe.GetVersion());
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_WebView2Probe_UserKeyPresent_Installed()
    {
        var probe = new WebView2RuntimeProbe(new FakeRegistryReader { UserValue = "121.0.0.0" });

        Assert.True(probe.IsInstalled());
        Assert.Equal("121.0.0.0", probe.GetVersion());
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_WebView2Probe_UsesDocumentedEvergreenRegistryKey()
    {
        Assert.Equal(
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            WebView2RuntimeProbe.ClientKey);
        Assert.Equal("pv", WebView2RuntimeProbe.VersionValueName);
    }

    /// <summary>
    /// The Evergreen runtime is installed by the 32-bit EdgeUpdate agent, so on x64 it registers
    /// under the WOW6432Node reflector. A 64-bit process reading only the native path reports the
    /// runtime as missing even though it is installed and working.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_WebView2Probe_AlsoChecksThe32BitRegistryView()
    {
        var registry = new FakeRegistryReader();
        var probe = new WebView2RuntimeProbe(registry);

        probe.GetVersion();

        Assert.Contains(WebView2RuntimeProbe.ClientKey, registry.MachineSubKeys);
        Assert.Contains(WebView2RuntimeProbe.Machine32BitClientKey, registry.MachineSubKeys);
    }
}

public sealed class OpenCodeProbeTests
{
    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_NotResolved_ReturnsNull()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(localAppData);
        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: string.Empty, openCodePathEnv: string.Empty);
            var probe = new OpenCodeProbe(identity);

            Assert.Null(probe.ResolvePath());
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_Resolved_ReturnsRuntimeIdentityPath()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        Directory.CreateDirectory(linksDir);
        var exe = Path.Combine(linksDir, "opencode.exe");
        File.WriteAllText(exe, "stub");
        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: string.Empty, openCodePathEnv: string.Empty);
            var probe = new OpenCodeProbe(identity);

            Assert.Equal(exe, probe.ResolvePath());
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_MissingMessage_IsPlainAndActionable()
    {
        Assert.Contains("opencode", OpenCodeProbe.MissingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Install", OpenCodeProbe.MissingMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", OpenCodeProbe.MissingMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A machine can have the OpenCode desktop app and still have no CLI, because the desktop app is
    /// a separate product that does not ship one. Telling such a user to "install opencode" is
    /// accurate but useless, so the message has to name the distinction.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_DesktopAppOnly_ExplainsThatTheDesktopAppIsNotTheCli()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var desktopDir = Path.Combine(localAppData, "Programs", "opencode");
        Directory.CreateDirectory(desktopDir);
        File.WriteAllText(Path.Combine(desktopDir, "OpenCode.exe"), "stub");
        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: string.Empty, openCodePathEnv: string.Empty);
            var probe = new OpenCodeProbe(identity);

            var message = probe.DescribeMissing();

            Assert.NotNull(message);
            Assert.Contains("desktop", message!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("command-line", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("npm install -g opencode-ai", message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_DescribeMissing_IsNullWhenTheCliIsPresent()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        Directory.CreateDirectory(linksDir);
        File.WriteAllText(Path.Combine(linksDir, "opencode.exe"), "stub");
        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: string.Empty, openCodePathEnv: string.Empty);
            var probe = new OpenCodeProbe(identity);

            Assert.Null(probe.DescribeMissing());
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    /// <summary>
    /// The CLI is installed by several package managers, each choosing its own directory, so PATH
    /// has to count. A machine with a perfectly good opencode on PATH must never be told to install it.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_CliOnPathOutsideKnownLocations_IsNotReportedMissing()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var shimDir = Path.Combine(localAppData, "scoop-shims");
        Directory.CreateDirectory(shimDir);
        File.WriteAllText(Path.Combine(shimDir, "opencode.exe"), "stub");
        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: shimDir, openCodePathEnv: string.Empty);
            var probe = new OpenCodeProbe(identity);

            Assert.Equal(Path.Combine(shimDir, "opencode.exe"), probe.ResolvePath());
            Assert.Null(probe.DescribeMissing());
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }

    /// <summary>
    /// "But it works in my terminal" is the usual report, so the message has to say where DevTeam
    /// looked. That is what makes the difference between a dead end and a fix.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-14")]
    public void REQ_14_OpenCodeProbe_MissingMessage_NamesTheDirectoryItSearched()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "devteam-tests-" + Guid.NewGuid());
        var shimDir = Path.Combine(localAppData, "npm");
        Directory.CreateDirectory(shimDir);
        try
        {
            var identity = RuntimeIdentity.Resolve([], null, null, @"C:\Users\tester", localAppData, pathEnv: shimDir, openCodePathEnv: string.Empty);
            var probe = new OpenCodeProbe(identity);

            var message = probe.DescribeMissing();

            Assert.NotNull(message);
            Assert.Contains(shimDir, message!);
            Assert.Contains("OPENCODE_PATH", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(localAppData, recursive: true);
        }
    }
}
