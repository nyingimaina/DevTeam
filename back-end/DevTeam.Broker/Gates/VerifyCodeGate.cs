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
        if (!failed)
            return GateResult.Pass("Tests passed", normalized);

        // A failure the operator already ruled on is settled, not a new defect. The test-runner
        // stage adjudicated this suite; re-failing the same red test here would send the developer
        // (who may not edit tests) round in a circle.
        var entries = TestOutputNormalizer.FailureEntries(raw);
        var accepted = AcceptedFailures(request, entries);
        var summaryFailures = TestOutputNormalizer.FailedCount(normalized);
        if (entries.Count > 0 && accepted.Count == entries.Count && summaryFailures <= accepted.Count)
        {
            return GateResult.Pass(
                "Tests passed apart from failures the operator accepted",
                normalized + "\n" + string.Join('\n', accepted.Select(name => $"accepted: {name}")));
        }

        return GateResult.Fail("Tests failed", normalized);
    }

    private static IReadOnlyList<string> AcceptedFailures(GateRequest request, IReadOnlyList<string> failures)
    {
        var reportPath = ArtifactPaths.TestReportPath(request.WorkspacePath, request.FeatureKey ?? string.Empty);
        if (failures.Count == 0 || !File.Exists(reportPath))
            return [];

        var accepted = TestReportReader.Parse(File.ReadAllText(reportPath))
            .Where(section => section.IsChallenge
                && TestAddendum.IsAccepted(TestRulings.EffectiveRuling(request.WorkspacePath, request.FeatureKey ?? string.Empty, section)))
            .ToArray();
        return failures
            .Where(failure => accepted.Any(section => TestReportReader.AccountsFor(section, failure)))
            .ToArray();
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