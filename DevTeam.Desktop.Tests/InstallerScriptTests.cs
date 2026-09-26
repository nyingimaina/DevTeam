namespace DevTeam.Desktop.Tests;

/// <summary>
/// Static verification of the InnoSetup installer script. These assert the installed behaviour the
/// installer promises (per-user install, prerequisite handling, shortcuts, uninstall prompt) by
/// inspecting the real packaging artifact.
/// </summary>
public sealed class InstallerScriptTests
{
    private static string Script => RepoPaths.ReadInstallerScript();

    [Fact]
    [Trait("Requirement", "REQ-1")]
    public void REQ_1_Installer_PackagesBrokerWebUiAndShellAsOneArtifact()
    {
        var script = Script;
        Assert.Contains("[Files]", script, StringComparison.Ordinal);
        Assert.Contains(@"Source: ""publish\app\*""", script, StringComparison.Ordinal);
        Assert.Contains("recursesubdirs", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DestDir: \"{app}\"", script, StringComparison.Ordinal);
        Assert.Contains("OutputBaseFilename=DevTeam-Setup-{#AppVersion}-win-x64", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-2")]
    public void REQ_2_Installer_PerUserInstall_NoElevation()
    {
        var script = Script;
        Assert.Contains(@"DefaultDirName={localappdata}\DevTeam", script, StringComparison.Ordinal);
        Assert.Contains("PrivilegesRequired=lowest", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivilegesRequired=admin", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-3")]
    public void REQ_3_Installer_DetectsAndOffersDotNet10Runtime()
    {
        var script = Script;
        Assert.Contains("IsDotNet10Installed", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.App", script, StringComparison.Ordinal);
        Assert.Contains("Microsoft.NETCore.App", script, StringComparison.Ordinal);
        Assert.Contains("dotnet.microsoft.com", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DotNetPage", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_Installer_DetectsWebView2RuntimeViaRegistry()
    {
        var script = Script;
        Assert.Contains("{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", script, StringComparison.Ordinal);
        Assert.Contains("IsWebView2Installed", script, StringComparison.Ordinal);
        Assert.Contains("WebView2Page", script, StringComparison.Ordinal);
        Assert.Contains("go.microsoft.com/fwlink", script, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Evergreen runtime is registered by the 32-bit EdgeUpdate agent, so on x64 it lives under
    /// the WOW6432Node reflector. Without checking that path the installer disagrees with the app
    /// about whether the runtime is present.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_Installer_AlsoChecksThe32BitRegistryView()
    {
        Assert.Contains("WOW6432Node", Script, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shell binary is x64, so the installer must not read the registry through a 32-bit view
    /// that silently redirects the lookup.
    /// </summary>
    [Fact]
    [Trait("Requirement", "REQ-4")]
    public void REQ_4_Installer_DeclaresItsArchitectureExplicitly()
    {
        var script = Script;

        Assert.Contains("ArchitecturesAllowed=x64compatible", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ArchitecturesInstallIn64BitMode=x64compatible", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-7")]
    public void REQ_7_Installer_UsesBuildVersionForArtifactName()
    {
        var script = Script;
        Assert.Contains("#ifndef AppVersion", script, StringComparison.Ordinal);
        Assert.Contains("AppVersion={#AppVersion}", script, StringComparison.Ordinal);
        Assert.Contains("DevTeam-Setup-{#AppVersion}-win-x64", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-8")]
    public void REQ_8_Installer_Uninstall_OffersToDeleteUserData()
    {
        var script = Script;
        Assert.Contains("CurUninstallStepChanged", script, StringComparison.Ordinal);
        Assert.Contains("usPostUninstall", script, StringComparison.Ordinal);
        Assert.Contains("DelTree", script, StringComparison.Ordinal);
        Assert.Contains(@"{userprofile}\.devteam", script, StringComparison.Ordinal);
        Assert.Contains(@"MsgBox(", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-10")]
    public void REQ_10_Installer_StartMenuEntryOnly_NoDesktopOrLoginEntry()
    {
        var script = Script;
        Assert.Contains(@"{autoprograms}\DevTeam", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{autodesktop}", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"CurrentVersion\Run", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RunOnce", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-15")]
    public void REQ_15_Installer_AddsNoFirewallRulesOrElevation()
    {
        var script = Script;
        Assert.DoesNotContain("netsh", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("New-NetFirewallRule", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("firewall", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ListenLocalhost", RepoPaths.ReadShellFile("..", "back-end", "DevTeam.Broker", "Program.cs"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-16")]
    public void REQ_16_Installer_PackagesNoSecrets()
    {
        var script = Script;
        Assert.DoesNotContain("git-credentials", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", script, StringComparison.Ordinal);
        Assert.DoesNotContain("password", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apiKey", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-18")]
    public void REQ_18_Installer_MessagesArePlainLanguage()
    {
        var script = Script;
        Assert.DoesNotContain("Exception", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stack trace", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Please install", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-19")]
    public void REQ_19_Installer_IsCompressed()
    {
        Assert.Contains("Compression=lzma2", Script, StringComparison.Ordinal);
        Assert.Contains("SolidCompression=yes", Script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-1")]
    public void REQ_1_BuildScript_VerifiesEveryWebAssetReferencedByIndexHtmlExists()
    {
        var script = RepoPaths.ReadBuildInstallerScript();

        Assert.Contains("Assert-WebUiAssetsPresent", script, StringComparison.Ordinal);
        Assert.Contains("/_next/", script, StringComparison.Ordinal);
        Assert.Contains("href", script, StringComparison.Ordinal);
        Assert.Contains("src", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-1")]
    public void REQ_1_BuildScript_RebuildsTheWebUiFromACleanExportDirectory()
    {
        var script = RepoPaths.ReadBuildInstallerScript();
        var cleanExport = script.IndexOf("Remove-DirectoryRobust (Join-Path $FrontendDir \"out\")", StringComparison.Ordinal);
        var frontendBuild = script.IndexOf("Assert-ExitCode \"npm run build\"", StringComparison.Ordinal);

        Assert.True(cleanExport >= 0, "build script must clean the Next.js export directory.");
        Assert.True(frontendBuild >= 0, "build script must run the Next.js build.");
        Assert.True(cleanExport < frontendBuild, "the export directory must be cleaned before the Next.js build runs.");
    }

    [Fact]
    [Trait("Requirement", "REQ-1")]
    public void REQ_1_BuildScript_CreatesTheWebRootBeforeCopyingTheExportIntoIt()
    {
        var script = RepoPaths.ReadBuildInstallerScript();
        var createWebRoot = script.IndexOf("New-Item -ItemType Directory -Force -Path $webRoot", StringComparison.Ordinal);
        var copyExport = script.IndexOf("Copy-Item -Path (Join-Path $FrontendOut \"*\") -Destination $webRoot", StringComparison.Ordinal);

        Assert.True(createWebRoot >= 0, "build script must create the destination wwwroot before copying into it.");
        Assert.True(copyExport >= 0, "build script must copy the exported web UI into the destination wwwroot.");
        Assert.True(createWebRoot < copyExport, "the destination wwwroot must exist before the recursive copy, or PowerShell flattens the export and every /_next/ asset 404s.");
    }

    [Fact]
    [Trait("Requirement", "REQ-1")]
    public void REQ_1_BuildScript_FailsLoudlyWhenADirectoryCannotBeRemoved()
    {
        var script = RepoPaths.ReadBuildInstallerScript();

        Assert.Contains("Failed to remove", script, StringComparison.Ordinal);
        Assert.Contains("if (Test-Path -LiteralPath $Path) { Fail", script, StringComparison.Ordinal);
    }
}
