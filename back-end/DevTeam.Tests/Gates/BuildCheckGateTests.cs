using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class BuildCheckGateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-buildcheck-" + Guid.NewGuid().ToString("N"));

    public BuildCheckGateTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Passes_WhenTheBuildSucceeds()
    {
        File.WriteAllText(Path.Combine(_workspace, "DevTeam.slnx"), "solution");
        var runner = FakeProcessRunner.Git("Build succeeded.");
        var gate = new BuildCheckGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.BuildCheck, _workspace, "feat-001"), CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | " + result.EvidenceText);
        Assert.Equal("dotnet", runner.Calls[0].FileName);
        Assert.Contains("build \"DevTeam.slnx\"", runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task Fails_WhenTheBuildErrors_WithoutParsingTestOutput()
    {
        // Unlike verify_code, this is a plain build — no "tests passed/failed" parsing, just
        // "did it compile". A scaffold-mismatch (wrong project reference, wrong target
        // framework) shows up here in seconds, before the much slower test run gets a chance to
        // fail for the same underlying reason.
        File.WriteAllText(Path.Combine(_workspace, "DevTeam.slnx"), "solution");
        var runner = FakeProcessRunner.Git(
            "error CS0246: The type or namespace name 'CalculatorService' could not be found", exitCode: 1);
        var gate = new BuildCheckGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.BuildCheck, _workspace, "feat-001"), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("CS0246", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheBuildTimesOut()
    {
        File.WriteAllText(Path.Combine(_workspace, "DevTeam.slnx"), "solution");
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(0, "still building…", "", true, TimeSpan.FromMinutes(3)));
        var gate = new BuildCheckGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.BuildCheck, _workspace, "feat-001"), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Timed out", result.Reason);
    }

    [Fact]
    public async Task Passes_WhenNoSolutionFileExistsYet()
    {
        // Nothing to build-check yet (e.g. the very first attempt before any scaffold exists) —
        // that's not this gate's failure to report; verify_code/app_launch cover that ground.
        var runner = FakeProcessRunner.Git("");
        var gate = new BuildCheckGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.BuildCheck, _workspace, "feat-001"), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task UsesAnExplicitBuildCommandInputWhenGiven()
    {
        File.WriteAllText(Path.Combine(_workspace, "DevTeam.slnx"), "solution");
        var runner = FakeProcessRunner.Git("Build succeeded.");
        var gate = new BuildCheckGate(runner);

        await gate.RunAsync(
            new GateRequest(BuiltinRegistry.BuildCheck, _workspace, "feat-001", "developer",
                new Dictionary<string, string> { ["buildCommand"] = "dotnet build CustomProject.csproj" }),
            CancellationToken.None);

        Assert.Contains("CustomProject.csproj", runner.Calls[0].Arguments);
    }
}
