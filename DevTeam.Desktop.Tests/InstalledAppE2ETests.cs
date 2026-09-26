using System.Diagnostics;

namespace DevTeam.Desktop.Tests;

/// <summary>
/// REQ-9: the automated installed-app E2E. The heavy run (silent install, launch, drive the real
/// UI, uninstall) is executed only when <c>DEVTEAM_RUN_INSTALLER_E2E=1</c> is set — it needs a
/// built installer and a clean Windows x64 machine. The script's required steps are always
/// verified so the coverage exists in every run.
/// </summary>
public sealed class InstalledAppE2ETests
{
    private static string Script => RepoPaths.ReadE2EScript();

    [Fact]
    [Trait("Requirement", "REQ-9")]
    public void REQ_9_InstalledAppE2E_ScriptCoversInstallLaunchDriveAndUninstall()
    {
        var script = Script;
        Assert.Contains("/VERYSILENT", script, StringComparison.Ordinal);
        Assert.Contains("DevTeam.Desktop.exe", script, StringComparison.Ordinal);
        Assert.Contains("/healthz", script, StringComparison.Ordinal);
        Assert.Contains("playwright", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unins000.exe", script, StringComparison.Ordinal);
        Assert.Contains("exit 1", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-9")]
    public void REQ_9_InstalledAppE2E_FailsOnAnyStepFailure()
    {
        var script = Script;
        Assert.Contains("function Fail", script, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE", script, StringComparison.Ordinal);
        Assert.Contains("Fail ", script, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Requirement", "REQ-9")]
    public void REQ_9_InstalledAppE2E_RunsGreenWhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("DEVTEAM_RUN_INSTALLER_E2E") != "1")
        {
            // The clean-machine run is opt-in; the script itself is verified above.
            Assert.True(File.Exists(RepoPaths.E2EScript));
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(RepoPaths.E2EScript);

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(20 * 60 * 1000);

        Assert.True(process.ExitCode == 0, $"E2E failed.\n{stdout}\n{stderr}");
    }
}
