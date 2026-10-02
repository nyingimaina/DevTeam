using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class TestRulingTests
{
    private const string Report = """
        # Test report

        ## TEST-1: Wizard > saves
        - Test: front-end/app/Wizard.test.tsx
        - Requirement: REQ-4
        - Verdict: challenge
        - Expected: Given a filled wizard, When I save, Then settings persist
        - Observed: the test expects a toast that the requirement never asked for
        - Proposed change: drop the toast assertion
        - Ruling: pending operator ruling

        ## TEST-2: Wizard > loads
        - Test: front-end/app/Wizard.test.tsx
        - Requirement: REQ-5
        - Verdict: fix
        - Expected: Given saved settings, When I open the wizard, Then they load
        - Observed: nothing loads

        ## TEST-3: Wizard > resets
        - Test: front-end/app/Wizard.test.tsx
        - Requirement: REQ-6
        - Verdict: challenge
        - Ruling: accepted (addendum BRS.addendum-1.md)
        """;

    private static string Workspace(string? report = Report)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-ruling-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, "feat-001"));
        if (report is not null)
            File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), report);
        return workspace;
    }

    [Fact]
    public void OnlyChallengesStillAwaitingARuling_AreListed_InPlainFields()
    {
        var pending = TestRulings.Pending(Workspace(), "feat-001");

        var one = Assert.Single(pending);
        Assert.Equal("Wizard > saves", one.Test);
        Assert.Equal("REQ-4", one.Requirement);
        Assert.Contains("settings persist", one.Expected);
        Assert.Contains("toast", one.Observed);
        Assert.Contains("drop the toast assertion", one.Details);
    }

    [Fact]
    public void WithNoReport_NothingIsPending()
    {
        Assert.Empty(TestRulings.Pending(Workspace(report: null), "feat-001"));
    }

    [Fact]
    public void AcceptingWritesAnAddendum_LinksTheBrs_AndRecordsTheRuling()
    {
        var workspace = Workspace();

        var result = TestRulings.Record(workspace, "feat-001", "Wizard > saves", RulingDecision.Accept);

        Assert.True(result.Ok, result.Problem);
        Assert.True(TestAddendum.Exists(workspace, "feat-001", result.AddendumId!.Value));
        Assert.Contains("drop the toast assertion", File.ReadAllText(TestAddendum.FilePath(workspace, "feat-001", result.AddendumId.Value)));
        Assert.True(BrsMutation.HasAddendumLink(File.ReadAllText(ArtifactPaths.BrsPath(workspace, "feat-001")), result.AddendumId.Value));
        Assert.Empty(TestRulings.Pending(workspace, "feat-001"));
    }

    [Fact]
    public void RejectingRecordsTheRuling_AndWritesNoAddendum()
    {
        var workspace = Workspace();

        var result = TestRulings.Record(workspace, "feat-001", "Wizard > saves", RulingDecision.Reject);

        Assert.True(result.Ok, result.Problem);
        Assert.Null(result.AddendumId);
        Assert.Empty(TestRulings.Pending(workspace, "feat-001"));
        Assert.False(File.Exists(ArtifactPaths.BrsPath(workspace, "feat-001")));
    }

    [Fact]
    public void ARulingSurvivesTheAgentRewritingTheReport()
    {
        var workspace = Workspace();
        TestRulings.Record(workspace, "feat-001", "Wizard > saves", RulingDecision.Reject);

        // The next test-runner turn writes a fresh report that, as far as it knows, is still pending.
        File.WriteAllText(ArtifactPaths.TestReportPath(workspace, "feat-001"), Report);

        Assert.Empty(TestRulings.Pending(workspace, "feat-001"));
        var section = TestReportReader.Parse(Report).First(s => s.Heading == "Wizard > saves");
        Assert.True(TestAddendum.IsRejected(TestRulings.EffectiveRuling(workspace, "feat-001", section)));
    }

    [Fact]
    public void AnAcceptedRulingReadsAsTheExactFormTheGateAccepts()
    {
        var workspace = Workspace();
        var id = TestRulings.Record(workspace, "feat-001", "Wizard > saves", RulingDecision.Accept).AddendumId!.Value;
        var section = TestReportReader.Parse(Report).First(s => s.Heading == "Wizard > saves");

        var ruling = TestRulings.EffectiveRuling(workspace, "feat-001", section);

        Assert.True(TestAddendum.IsAccepted(ruling));
        Assert.Contains(TestAddendum.FileName(id), ruling);
    }

    [Fact]
    public void WithoutARecordedRuling_TheReportsOwnWordsStand()
    {
        var workspace = Workspace();
        var section = TestReportReader.Parse(Report).First(s => s.Heading == "Wizard > resets");

        Assert.Equal("accepted (addendum BRS.addendum-1.md)", TestRulings.EffectiveRuling(workspace, "feat-001", section));
    }

    [Fact]
    public void RulingOnATestThatIsNotPending_IsRefused()
    {
        var result = TestRulings.Record(Workspace(), "feat-001", "Wizard > loads", RulingDecision.Accept);

        Assert.False(result.Ok);
        Assert.Contains("not waiting", result.Problem);
    }

    [Fact]
    public void EachAcceptedRulingGetsItsOwnAddendum()
    {
        var report = Report.Replace("## TEST-3: Wizard > resets", "## TEST-3: Wizard > resets")
            .Replace("- Ruling: accepted (addendum BRS.addendum-1.md)", "- Ruling: pending operator ruling");
        var workspace = Workspace(report);

        var first = TestRulings.Record(workspace, "feat-001", "Wizard > saves", RulingDecision.Accept);
        var second = TestRulings.Record(workspace, "feat-001", "Wizard > resets", RulingDecision.Accept);

        Assert.NotEqual(first.AddendumId, second.AddendumId);
    }
}
