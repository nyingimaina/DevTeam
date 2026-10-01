using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class TestRunGateTests
{
    private static string CreateWorkspace(string? testCommand = "dotnet test DevTeam.slnx")
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-testrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, "feat-001"));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(workspace, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/**/Features/Login", "front-end/app/login", [], testCommand!));
        return workspace;
    }

    [Fact]
    public async Task WritesMachineFailures_AndStillPasses_SoTheReportStepCanRun()
    {
        var workspace = CreateWorkspace();
        var runner = FakeProcessRunner.Git("Tests: 0 failed, 12 passed, 12 total\n");
        var gate = new TestRunGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestRun, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason);
        var run = TestRunIO.TryRead(workspace, "feat-001");
        Assert.NotNull(run);
        Assert.Equal(0, run!.FailedCount);
        Assert.Empty(run.Failures);
        Assert.Equal("dotnet test DevTeam.slnx", run.Command);
    }

    [Fact]
    public async Task RecordsRealFailures_WithoutFailingTheRunStep()
    {
        var workspace = CreateWorkspace();
        var runner = FakeProcessRunner.Git(
            "  \u25CF Wizard \u203A saves\nTests: 1 failed, 11 passed, 12 total\n", exitCode: 1);
        var gate = new TestRunGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestRun, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason);
        var run = TestRunIO.TryRead(workspace, "feat-001");
        Assert.Equal(1, run!.FailedCount);
        Assert.Equal(["Wizard \u203A saves"], run.Failures);
    }

    [Fact]
    public async Task VendorNoise_IsNotRecordedAsAFailure()
    {
        var workspace = CreateWorkspace();
        var runner = FakeProcessRunner.Git(string.Join('\n',
        [
            "Tests: 0 failed, 120 passed, 120 total",
            "    at node_modules/react-dom/cjs/react-dom.development.js:45851:13",
        ]), exitCode: 0);
        var gate = new TestRunGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestRun, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason);
        Assert.Empty(TestRunIO.TryRead(workspace, "feat-001")!.Failures);
    }

    [Fact]
    public async Task Fails_WhenTheRunItselfCouldNotComplete()
    {
        var workspace = CreateWorkspace();
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(0, string.Empty, string.Empty, true, TimeSpan.FromMinutes(9)));
        var gate = new TestRunGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestRun, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Timed out", result.Reason);
    }
}

public class TestReportGateTests
{
    private static string CreateWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, "feat-001"));
        return workspace;
    }

    private static void WriteRun(string workspace, params string[] failures)
        => TestRunIO.Write(workspace, "feat-001", new TestRunRecord(
            "dotnet test DevTeam.slnx", 0, failures.Length, failures, false, DateTimeOffset.UtcNow));

    private const string GreenReport = """
        # Test report - feat-001

        Result: 12 passed, 0 failed. Nothing to adjudicate.
        """;

    private const string OneFailureReport = """
        # Test report - feat-001

        Result: 11 passed, 1 failed.

        ## TEST-1: Wizard > saves
        - Test: front-end/app/Wizard.test.tsx
        - Requirement: REQ-4
        - Verdict: fix
        - Expected: Given a filled wizard, When I save, Then settings persist
        - Observed: save returns 500, nothing persisted
        - Likely cause: the save handler throws before the fetch
        - Classification: introduced by this feature
        """;

    [Fact]
    public async Task Passes_WhenTheRunWasGreen()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace);
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), GreenReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenAFailureHasNoSectionInTheReport()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), GreenReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Wizard > saves", result.EvidenceText);
        Assert.Contains("no section", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenASectionHasNoVerdict()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"),
            "# Test report\n\n## TEST-1: Wizard > saves\n- Test: front-end/app/Wizard.test.tsx\n");
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Verdict", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenAVerdictIsNeitherFixNorChallenge()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), OneFailureReport.Replace("Verdict: fix", "Verdict: probably fine"));
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("fix", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_AndRoutesToDeveloper_WhenAFailureIsAdjudicatedAsFix()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), OneFailureReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner",
                new Dictionary<string, string>
                {
                    ["responsibleRole"] = "developer",
                }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("TEST-1", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenAChallengeIsStillAwaitingAnOperatorRuling()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(pending: true));
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("operator ruling", result.EvidenceText);
    }

    [Fact]
    public async Task Passes_WhenAChallengeWasAcceptedAndTheAddendumExists()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(pending: false));
        TestAddendum.Write(workspace, "feat-001", 1,
            "## REQ-4 (revised)\nGiven a filled wizard, When I save, Then settings persist.\n");
        BrsMutation.AppendAddendumLink(workspace, "feat-001", 1);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheChallengeWasAccepted_ButTheBrsHasNoAddendumLink()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(pending: false));
        TestAddendum.Write(workspace, "feat-001", 1, "## REQ-4 (revised)\n");
        File.WriteAllText(ArtifactPaths.BrsPath(workspace, "feat-001"), "## REQ-4: Save\nGiven x, When y, Then z\n");
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("link", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheChallengeWasAccepted_ButTheAddendumFileIsMissing()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(pending: false));
        BrsMutation.AppendAddendumLink(workspace, "feat-001", 1);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("addendum", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheChallengeWasRejected_EvenThoughItIsFullyDocumented()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(rejected: true));
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("rejected", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheRunDataIsMissing()
    {
        var workspace = CreateWorkspace();
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), GreenReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("test_run", result.Reason + result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenATestFileChangedInThisFeature_ButTheRunWasGreen()
    {
        // The silent-rewrite case the challenge protocol exists to catch: a green suite after
        // editing a test file, with nothing in the report explaining why.
        var workspace = CreateWorkspace();
        WriteRun(workspace);
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), GreenReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner",
                new Dictionary<string, string>
                {
                    ["changedTestFiles"] = "front-end/app/Wizard.test.tsx",
                }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("without an approved challenge", result.EvidenceText);
        Assert.Contains("front-end/app/Wizard.test.tsx", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenATestFileChangedInThisFeature_WithOnlyAFixVerdict()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), OneFailureReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner",
                new Dictionary<string, string>
                {
                    ["changedTestFiles"] = "front-end/app/Wizard.test.tsx",
                }),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("without an approved challenge", result.EvidenceText);
    }

    [Fact]
    public async Task Passes_WhenTheChangedTestFilesAllHaveAnAcceptedChallenge()
    {
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(pending: false));
        TestAddendum.Write(workspace, "feat-001", 1, "## REQ-4 (revised)\n");
        BrsMutation.AppendAddendumLink(workspace, "feat-001", 1);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner",
                new Dictionary<string, string>
                {
                    ["changedTestFiles"] = "front-end/app/Wizard.test.tsx",
                }),
            CancellationToken.None);

        Assert.True(result.Passed, result.Reason + " | `n" + result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenAChallengeIsAccepted_ButNoAddendumIsNamed()
    {
        // "Accepted" on its own is not a ruling: the approved change has to exist as a document,
        // or the LLM has granted itself a requirement change nobody approved.
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), ChallengeReport(ruling: "- Ruling: accepted"));
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("addendum", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenAChallengeCarriesARulingNobodyCanParse()
    {
        // An unrecognised ruling must never read as a pass: a typo in the ruling line would
        // otherwise wave a disputed requirement straight through.
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"),
            ChallengeReport(ruling: "- Ruling: the operator said it's fine I think"));
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("operator ruling", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenASectionAccountsForNoRecordedFailure()
    {
        // The inverse of "every failure needs a section": a section may only account for a failure
        // the machine run actually recorded. Without this, a green run can be held hostage by a
        // challenge for a test that never failed - and the only ways out are accepting an addendum
        // for a non-failure or sending the developer to implement a non-failure.
        var workspace = CreateWorkspace();
        WriteRun(workspace); // green: zero recorded failures
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), OrphanSectionReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("recording defect", result.EvidenceText);
        Assert.Contains("Scroller", result.EvidenceText);
    }

    [Fact]
    public async Task AnOrphanSectionIsReportedAsADefect_NotAsAnUnaccountedFailure()
    {
        // Both directions are wrong in different ways and the operator has to be able to tell them
        // apart: an unaccounted failure means the report forgot a real failure; a recording defect
        // means the report invented one.
        var workspace = CreateWorkspace();
        WriteRun(workspace); // green, so nothing is unaccounted
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), OrphanSectionReport);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.DoesNotContain("no section in the report", result.EvidenceText);
    }

    [Fact]
    public async Task ASectionIsAccepted_WhenItAccountsForARecordedFailureAmongSeveral()
    {
        // Guards against the defect check being sloppy in the other direction: a report that
        // accounts for a real failure AND mentions an unrelated passing test in passing prose must
        // still pass, or ordinary narrative kills the stage.
        var workspace = CreateWorkspace();
        WriteRun(workspace, "Wizard > saves");
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"),
            """
            # Test report - feat-001

            Result: 11 passed, 1 failed. The suite also covers "Scroller > scrolls", which passed.

            ## TEST-1: Wizard > saves
            - Test: front-end/app/Wizard.test.tsx
            - Requirement: REQ-4
            - Verdict: fix
            - Expected: Given a filled wizard, When I save, Then settings persist
            - Observed: save returns 500, nothing persisted
            - Likely cause: the save handler throws before the fetch
            - Classification: introduced by this feature
            """);
        var gate = new TestReportGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.TestReport, workspace, "feat-001", "test-runner"),
            CancellationToken.None);

        // Still fails, but as a routed code defect - not as a recording defect.
        Assert.False(result.Passed);
        Assert.DoesNotContain("recording defect", result.EvidenceText);
    }

    private const string OrphanSectionReport = """
        # Test report - feat-001

        Result: 12 passed, 0 failed. One flaky failure seen on a re-run is reported below.

        ## TEST-1: Scroller > scrolls to the bottom
        - Test: front-end/app/Scroller.test.tsx
        - Requirement: none
        - Verdict: challenge
        - Ruling: pending operator ruling
        """;

    private static string ChallengeReport(bool pending = false, bool rejected = false, string? ruling = null)
    {
        ruling ??= pending
            ? "- Ruling: pending operator ruling"
            : rejected
                ? "- Ruling: rejected - implement REQ-4 as written"
                : "- Ruling: accepted (addendum BRS.addendum-1.md)";
        return $"""
            # Test report - feat-001

            Result: 11 passed, 1 failed.

            ## TEST-1: Wizard > saves
            - Test: front-end/app/Wizard.test.tsx
            - Requirement: REQ-4
            - Verdict: challenge
            - Challenge: this test asserts a 2 second timeout that REQ-4 never specified
            - Proposed resolution: assert persistence only
            {ruling}
            """;
    }
}
