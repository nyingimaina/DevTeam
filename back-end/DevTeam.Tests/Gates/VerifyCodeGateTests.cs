using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class VerifyCodeGateTests
{
    [Fact]
    public async Task Passes_WhenExitCodeIsZeroAndNoFailures()
    {
        var runner = FakeProcessRunner.Git(
            "build output...\nPassed!  - Failed: 0, Passed: 42, Skipped: 0, Total: 42, Duration: 8 s");
        var gate = new VerifyCodeGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.VerifyCode, @"C:\work\proj", "feat-001", "developer", new Dictionary<string, string>
            {
                ["commandLine"] = "dotnet test DevTeam.slnx",
            }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
        Assert.Contains("tests: 42 passed", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTestsFailDespiteZeroExitCode()
    {
        var runner = FakeProcessRunner.Git(
            "Test Suites: 1 failed, 8 passed, 9 total\nTests: 7 failed, 38 passed, 45 total", exitCode: 0);
        var gate = new VerifyCodeGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.VerifyCode, @"C:\work\proj", "feat-001", "developer"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("38 passed, 7 failed", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenDotnetReportsAFailedTestWithoutAnySummary()
    {
        var runner = FakeProcessRunner.Git(
            "  Failed DevTeam.Tests.WorkflowDefinitionLoaderTests.Load_AgentWithoutMode_Throws [11 ms]", exitCode: 0);
        var gate = new VerifyCodeGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.VerifyCode, @"C:\work\proj", "feat-001", "developer"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("fail: DevTeam.Tests.WorkflowDefinitionLoaderTests", result.EvidenceText);
    }

    [Fact]
    public async Task Passes_WhenAllTestsPass_ButReactActWarningsAndVendorFramesArePrinted()
    {
        var runner = FakeProcessRunner.Git(string.Join('\n',
        [
            "Tests:       0 failed, 120 passed, 120 total",
            "  console.error",
            "    Warning: An update to ZestButton inside a test was not wrapped in act(...)",
            "    at node_modules/react-dom/cjs/react-dom-test-utils.development.js:129:18",
            "    at ZestResponsiveLayout.test.tsx:41:12",
        ]), exitCode: 0);
        var gate = new VerifyCodeGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.VerifyCode, @"C:\work\proj", "feat-001", "developer"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenProcessTimesOut()
    {
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(0, "slow build...", "", true, TimeSpan.FromMinutes(3)));
        var gate = new VerifyCodeGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.VerifyCode, @"C:\work\proj", "feat-001", "developer"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Timed out", result.Reason);
    }

    [Fact]
    public async Task UsesDefaultManifestTestCommand_WhenRequestOmitsIt()
    {
        var workspace = CreateWorkspaceWithManifest();
        var runner = FakeProcessRunner.Git("Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1");
        var gate = new VerifyCodeGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.VerifyCode, workspace, "feat-001"),
            CancellationToken.None);

Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
        Assert.Equal("dotnet", runner.Calls[0].FileName);
        Assert.Contains("test DevTeam.slnx", runner.Calls[0].Arguments);
    }

    private static string CreateWorkspaceWithManifest()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, "feat-001"));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(workspace, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/**/Features/Login", "front-end/app/login", [], "dotnet test DevTeam.slnx"));
        return workspace;
    }
}

