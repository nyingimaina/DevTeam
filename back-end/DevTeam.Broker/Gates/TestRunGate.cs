using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Runs the feature's full test command once and writes the machine-extracted result to
/// devteam/features/&lt;F&gt;/test-run.json - the facts the test-runner agent authors its report
/// from.
/// <para>
/// It passes even when tests fail, on purpose: a failing run is exactly when the report is most
/// needed, and failing here would route straight back to the developer without ever producing
/// the artifact that makes the failure actionable (and challengeable). Only a run that couldn't
/// complete - timeout, no command - fails, because then there are no facts to write.
/// </para>
/// </summary>
public sealed class TestRunGate : IGate
{
    private readonly IProcessRunner _runner;

    public TestRunGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.TestRun;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var workspace = request.WorkspacePath;
        var featureKey = request.FeatureKey ?? string.Empty;
        var commandLine = GateInputs.GetOptional(request.Inputs, "commandLine")
            ?? SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(workspace, featureKey))?.TestCommand
            ?? "dotnet test DevTeam.slnx";
        var workingDirectory = GateInputs.Get(request.Inputs, "workingDirectory", workspace);
        var (fileName, arguments) = CommandInvocation.ForNpmShim(commandLine);

        var result = await _runner.RunAsync(
            new ProcessRunRequest(fileName, arguments, workingDirectory, TimeoutMs),
            cancellationToken);

        if (result.TimedOut)
            return GateResult.Fail("Timed out waiting for the test run to finish.", $"test run exceeded {TimeoutMs / 1000}s: {commandLine}");

        var raw = result.StandardOutput + "\n" + result.StandardError;
        var normalized = TestOutputNormalizer.Normalize(raw);
        var failures = TestOutputNormalizer.FailureEntries(raw);
        var failedCount = Math.Max(TestOutputNormalizer.FailedCount(normalized), failures.Count);

        TestRunIO.Write(workspace, featureKey, new TestRunRecord(
            commandLine, result.ExitCode, failedCount, failures, false, DateTimeOffset.UtcNow));

        var summary = failedCount == 0
            ? $"tests: {normalized.Split('\n').FirstOrDefault(l => l.StartsWith("tests:", StringComparison.Ordinal)) ?? "passed"}"
            : $"{failedCount} failed test(s): {string.Join(", ", failures.Take(5))}";

        return GateResult.Pass(
            failedCount == 0 ? "Test run completed with no failures" : "Test run completed with failures",
            summary,
            TestRunIO.FilePath(workspace, featureKey));
    }

    private const int TimeoutMs = 1_800_000;
}
