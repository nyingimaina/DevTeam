using System.Text.RegularExpressions;

using DevTeam.Broker.Workflow;

namespace DevTeam.Broker.Gates;

/// <summary>
/// The deterministic verdict on the test-runner's report. It passes only when every failure the
/// machine run recorded is accounted for and resolved:
/// <list type="number">
/// <item>green run and a report that says so;</item>
/// <item>a failure judged <c>fix</c> - the requirement stands, the code is wrong, so this gate
/// fails and routes back to the developer;</item>
/// <item>a failure judged <c>challenge</c> and ruled on by the operator: accepted means a
/// BRS addendum exists and the BRS links to it, rejected means the requirement stands and the
/// developer must implement it as written;</item>
/// <item>no test file changed in this feature's diff without an approved challenge covering
/// that exact file - the anti-silent-rewrite rule.</item>
/// </list>
/// The operator's ruling is the only thing that can unblock a disputed requirement, and the LLM
/// only records it: the report says what was proposed, the addendum is the approved change.
/// </summary>
public sealed class TestReportGate : IGate
{
    private readonly GitChangeSet? _changes;

    public TestReportGate(IProcessRunner? runner = null) => _changes = runner is null ? null : new GitChangeSet(runner);

    public string Name => BuiltinRegistry.TestReport;

    public async Task<GateResult> RunAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var changedTestFiles = await ChangedTestFilesAsync(request, cancellationToken);
        return Evaluate(request, changedTestFiles);
    }

    private static GateResult Evaluate(GateRequest request, IReadOnlyList<string> changedTestFiles)
    {
        var workspace = request.WorkspacePath;
        var featureKey = request.FeatureKey ?? string.Empty;

        var run = TestRunIO.TryRead(workspace, featureKey);
        if (run is null)
            return GateResult.Fail(
                "No test run data to review",
                $"test_run must produce {TestRunIO.FileName} before a report can be judged.");

        var reportPath = ArtifactPaths.TestReportPath(workspace, featureKey);
        if (!File.Exists(reportPath))
            return GateResult.Fail("No test report to review", $"expected {reportPath}");

        var sections = TestReportReader.Parse(File.ReadAllText(reportPath));
        var evidence = new System.Text.StringBuilder();
        evidence.Append($"test run: {run.FailedCount} failed of the recorded run\n");
        evidence.Append($"report: {sections.Count} section(s)\n");

        var unaccounted = run.Failures
            .Where(failure => !sections.Any(section => TestReportReader.AccountsFor(section, failure)))
            .ToArray();
        if (unaccounted.Length > 0)
        {
            foreach (var failure in unaccounted)
                evidence.Append($"fail: {failure} has no section in the report\n");
            return GateResult.Fail(
                "The report does not account for every failed test",
                evidence.ToString(),
                reportPath);
        }

        var problems = new List<string>();
        var pendingFixes = new List<string>();

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Verdict))
            {
                problems.Add($"{Heading(section)} has no \"Verdict: fix\" or \"Verdict: challenge\" line.");
                continue;
            }

            if (!section.IsFix && !section.IsChallenge)
            {
                problems.Add($"{Heading(section)} has Verdict: {section.Verdict} - only fix or challenge are allowed.");
                continue;
            }

            if (section.IsFix)
            {
                pendingFixes.Add(Heading(section));
                continue;
            }

            if (TestAddendum.IsRejected(section.Ruling))
            {
                problems.Add(
                    $"{Heading(section)} challenged the requirement and the operator rejected it: implement the " +
                    "requirement exactly as the BRS states it. No further discussion.");
                continue;
            }

            if (section.Ruling is null || section.Ruling.Contains("pending", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{Heading(section)} is a challenge awaiting an operator ruling. The requirement stands until " +
                    "the operator rules; the ruling is recorded as \"Ruling: accepted (addendum <file>)\" or " +
                    "\"Ruling: rejected - implement as written\".");
                continue;
            }

            var addendumIds = AddendumIds(section);
            if (!TestAddendum.IsAccepted(section.Ruling) || addendumIds.Count == 0)
            {
                // Falling through to a pass here would let a challenge wave itself through on a
                // loosely worded ruling ("accepted", "looks fine to me") that names no approved
                // change. An unrecognised ruling is a pending ruling: fail closed.
                problems.Add(
                    $"{Heading(section)} has a ruling that is not an operator ruling: \"{section.Ruling}\". " +
                    $"The gate only accepts \"Ruling: accepted (addendum {TestAddendum.FileName(1)})\" - naming the " +
                    "addendum that holds the approved change - or \"Ruling: rejected - implement as written\". " +
                    "Record the operator's actual words; do not paraphrase them into a decision.");
                continue;
            }

            foreach (var id in addendumIds)
            {
                if (!TestAddendum.Exists(workspace, featureKey, id))
                    problems.Add($"{Heading(section)} was accepted but {TestAddendum.FileName(id)} does not exist.");
                else if (!BrsMutation.HasAddendumLink(ReadBrs(workspace, featureKey), id))
                    problems.Add(
                        $"{Heading(section)} was accepted but the BRS has no \"- See addendum: {TestAddendum.FileName(id)}\" link.");
            }
        }

        var silentTestEdits = changedTestFiles
            .Where(file => !sections.Any(section => section.IsChallenge
                && section.TestFile is not null
                && NormalizePath(section.TestFile).EndsWith(NormalizePath(file), StringComparison.OrdinalIgnoreCase)
                && TestAddendum.IsAccepted(section.Ruling)))
            .ToArray();
        foreach (var file in silentTestEdits)
            problems.Add(
                $"{file} was changed in this feature's diff without an approved challenge. A changed test file " +
                "needs a challenge section naming it, ruled on by the operator, with the approved change written " +
                "as a BRS addendum.");

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
                evidence.Append("fail: ").Append(problem).Append('\n');
            return GateResult.Fail("Test report needs work before the feature can proceed", evidence.ToString(), reportPath);
        }

        if (pendingFixes.Count > 0)
        {
            foreach (var fix in pendingFixes)
                evidence.Append("fail: ").Append(fix).Append(" - requirement stands, the code is wrong: implement it.\n");
            return GateResult.Fail(
                "Failed tests are adjudicated as code defects",
                evidence.ToString(),
                reportPath);
        }

        return GateResult.Pass("Every failure is accounted for", evidence.ToString(), reportPath);
    }

    private static string ReadBrs(string workspace, string featureKey)
    {
        var path = ArtifactPaths.BrsPath(workspace, featureKey);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static IReadOnlyList<int> AddendumIds(TestReportSection section)
    {
        var ids = new List<int>();
        if (section.Ruling is null)
            return ids;
        foreach (Match match in Regex.Matches(
            section.Ruling, $@"{Regex.Escape(TestAddendum.Prefix)}(\d+)\.md", RegexOptions.IgnoreCase))
        {
            if (int.TryParse(match.Groups[1].Value, out var id))
                ids.Add(id);
        }

        return ids;
    }

    /// <summary>
    /// The test files this feature touched: an explicit input when the caller knows them (tests,
    /// and any caller with a cheaper source than git), otherwise the git change set.
    /// </summary>
    private async Task<IReadOnlyList<string>> ChangedTestFilesAsync(GateRequest request, CancellationToken cancellationToken)
    {
        var declared = GateInputs.GetOptional(request.Inputs, "changedTestFiles");
        var paths = declared is null
            ? _changes is null
                ? []
                : await _changes.ChangedPathsAsync(
                    request.WorkspacePath, GateInputs.GetOptional(request.Inputs, "baseRef"), cancellationToken)
            : ChangedFiles.Parse(declared.Replace(';', '\n'));

        return paths.Where(TestDiscovery.LooksLikeTestFile).ToArray();
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().TrimStart('.').TrimStart('/');

    private static string Heading(TestReportSection section) => $"TEST-{section.Number} \"" + section.Heading + "\"";
}
