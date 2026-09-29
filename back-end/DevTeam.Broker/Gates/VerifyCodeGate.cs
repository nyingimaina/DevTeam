using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

public sealed class VerifyCodeGate : IGate
{
    private readonly IProcessRunner _runner;

    public VerifyCodeGate(IProcessRunner runner) => _runner = runner;

    public string Name => BuiltinRegistry.VerifyCode;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var commandLine = GateInputs.Get(request.Inputs, "commandLine",
            SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(request.WorkspacePath, request.FeatureKey ?? string.Empty))?.TestCommand ?? "dotnet test DevTeam.slnx");
        var workingDirectory = GateInputs.Get(request.Inputs, "workingDirectory", request.WorkspacePath);
        var (fileName, arguments) = SplitCommand(commandLine);

        var result = await _runner.RunAsync(new ProcessRunRequest(fileName, arguments, workingDirectory), cancellationToken);

        if (result.TimedOut)
            return GateResult.Fail("Timed out waiting for tests to finish.", "Tests exceeded the time budget.");

        var raw = result.StandardOutput + "\n" + result.StandardError;
        var normalized = TestOutputNormalizer.Normalize(raw);
        // Three independent signals, none of them "the text contains fail:": a nonzero exit
        // code, a summary line that counts failures, and the parsed failure entries. Matching
        // the rendered marker instead would let an unrelated gate's "fail:" text decide a
        // test verdict, and would re-raise every file:line a green run happens to print.
        var failed = result.ExitCode != 0
            || TestOutputNormalizer.FailedCount(normalized) > 0
            || TestOutputNormalizer.FailureEntries(raw).Count > 0;
        return failed
            ? GateResult.Fail($"Tests failed", normalized)
            : GateResult.Pass("Tests passed", normalized);
    }

    private static (string FileName, string Arguments) SplitCommand(string commandLine)
    {
        var trimmed = commandLine.Trim();
        var separator = trimmed.IndexOf(' ');
        if (separator <= 0)
            return (trimmed, string.Empty);
        return (trimmed[..separator], trimmed[(separator + 1)..]);
    }
}