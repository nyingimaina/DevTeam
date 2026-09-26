namespace DevTeam.Desktop.Tests;

/// <summary>
/// Static verification of the standalone installer build script and the preserved developer
/// workflow (REQ-12, REQ-13, REQ-19).
/// </summary>
public sealed class InstallerBuildScriptTests
{
    private static string Script => RepoPaths.ReadBuildInstallerScript();

    [Fact]
    [Trait("Requirement", "REQ-7")]
    public void REQ_7_BuildScript_ReadsVersionFromDirectoryBuildProps()
    {
        var script = Script;
        Assert.Contains("Directory.Build.props", script, StringComparison.Ordinal);
        Assert.Contains("InformationalVersion", script, StringComparison.Ordinal);
        Assert.Contains("/DAppVersion=$version", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-12")]
    public void REQ_12_BuildScript_BuildsFrontendPublishesBrokerAndShellThenRunsIscc()
    {
        var script = Script;
        Assert.Contains("npm run build", script, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", script, StringComparison.Ordinal);
        Assert.Contains("DevTeam.Broker", script, StringComparison.Ordinal);
        Assert.Contains("DevTeam.Desktop", script, StringComparison.Ordinal);
        Assert.Contains("iscc", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-12")]
    public void REQ_12_BuildScript_FailsLoudlyAndChecksArtifact()
    {
        var script = Script;
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ExitCode", script, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE", script, StringComparison.Ordinal);
        Assert.Contains("DevTeam-Setup-$version-win-x64.exe", script, StringComparison.Ordinal);
        Assert.Contains("Test-Path $artifact", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-12")]
    public void REQ_12_BuildScript_IsStandalone_DoesNotCallRootBuildScript()
    {
        var script = Script;
        Assert.DoesNotContain("build.ps1", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-19")]
    public void REQ_19_BuildScript_PublishesFrameworkDependent()
    {
        var script = Script;
        Assert.Contains("--self-contained false", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--self-contained true", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-13")]
    public void REQ_13_ExistingDeveloperBuildScript_StillPublishesBroker()
    {
        var root = RepoPaths.Root;
        var buildScript = File.ReadAllText(Path.Combine(root, "build.ps1"));
        Assert.Contains("dotnet publish", buildScript, StringComparison.Ordinal);
        Assert.Contains("DevTeam.Broker", buildScript, StringComparison.Ordinal);
        Assert.Contains("publish\\broker", buildScript, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-16")]
    public void REQ_16_PackagingInputs_ContainNoSecrets()
    {
        foreach (var path in new[]
        {
            RepoPaths.InstallerScript,
            RepoPaths.BuildInstallerScript,
            RepoPaths.E2EScript
        })
        {
            var content = File.ReadAllText(path);
            Assert.DoesNotContain("git-credentials", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN RSA PRIVATE KEY", content, StringComparison.Ordinal);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);
        }
    }
}
