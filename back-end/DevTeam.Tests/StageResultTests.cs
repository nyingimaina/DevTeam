using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class StageResultTests
{
    private static string Workspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-stageresult-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, "feat-001"));
        return workspace;
    }

    private static void Write(string workspace, string json)
        => File.WriteAllText(StageResultIO.PathFor(workspace, "feat-001", "developer"), json);

    [Fact]
    public void ReadsAValidResult()
    {
        var workspace = Workspace();
        Write(workspace, """
            { "verdict": "done", "summary": "Added the sum rule in Calc.cs; REQ-1..3 covered.",
              "points": [ { "n": 1, "kind": "ADDRESSED", "detail": "added REQ_2 test" },
                          { "n": 2, "kind": "disputed", "detail": "the coverage check misses nested describes" } ] }
            """);

        var read = StageResultIO.TryRead(workspace, "feat-001", "developer");

        Assert.True(read.Ok, read.Problem);
        Assert.Equal(StageVerdict.Done, read.Result!.Verdict);
        Assert.Contains("sum rule", read.Result.Summary);
        Assert.Equal(2, read.Result.Points.Count);
        Assert.Equal(ReviewFindingResponse.Addressed, read.Result.Points[0].Kind);
        Assert.Equal(ReviewFindingResponse.Disputed, read.Result.Points[1].Kind);
    }

    [Fact]
    public void AMissingFile_IsNotOk_AndSaysSo()
    {
        var read = StageResultIO.TryRead(Workspace(), "feat-001", "developer");

        Assert.False(read.Ok);
        Assert.Contains("stage-result.developer.json", read.Problem);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "verdict": "done" }""")]
    [InlineData("""{ "verdict": "done", "summary": "   " }""")]
    [InlineData("""{ "verdict": "maybe", "summary": "x" }""")]
    [InlineData("""{ "verdict": "done", "summary": "x", "points": [ { "n": 1, "kind": "shrug" } ] }""")]
    public void AnInvalidResult_IsRejectedWithAReasonTheAgentCanAct_On(string json)
    {
        var workspace = Workspace();
        Write(workspace, json);

        var read = StageResultIO.TryRead(workspace, "feat-001", "developer");

        Assert.False(read.Ok);
        Assert.False(string.IsNullOrWhiteSpace(read.Problem));
    }

    [Fact]
    public void PointAnswers_AreMatchedByNumber_AndUnmentionedPointsStayUnanswered()
    {
        var points = new[] { new ReviewFinding(), new ReviewFinding(), new ReviewFinding() };
        var result = new StageResult(StageVerdict.Done, "x",
        [
            new StagePointAnswer(3, ReviewFindingResponse.Blocked, "no test project"),
            new StagePointAnswer(1, ReviewFindingResponse.Addressed, "renamed tests"),
        ]);

        var answers = StageResultIO.AnswerPoints(result, points);

        Assert.Equal(ReviewFindingResponse.Addressed, answers[0].Kind);
        Assert.Equal(ReviewFindingResponse.None, answers[1].Kind);
        Assert.Equal(ReviewFindingResponse.Blocked, answers[2].Kind);
        Assert.Equal("no test project", answers[2].Detail);
    }

    [Fact]
    public void ASynthesizedResult_IsMarked_SoItIsNeverMistakenForTheAgentsOwnWords()
    {
        var result = StageResultIO.Synthesize("Did the thing. All good.");

        Assert.True(result.Synthesized);
        Assert.Equal(StageVerdict.Done, result.Verdict);
        Assert.Contains("Did the thing", result.Summary);
    }

    [Fact]
    public void ThePreviousStagesResult_BecomesTheNextStagesContext()
    {
        var workspace = Workspace();
        Write(workspace, """{ "verdict": "blocked", "summary": "REQ-4 needs a decision on rounding." }""");

        var context = StageResultIO.BuildContext(workspace, "feat-001", "developer");

        Assert.Contains("developer", context);
        Assert.Contains("blocked", context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("REQ-4 needs a decision on rounding.", context);
    }

    [Fact]
    public async Task TheHandoffDocument_CarriesWhatEachStageConcluded_NotATemplate()
    {
        var workspace = Workspace();
        StageResultIO.Write(workspace, "feat-001", "developer",
            new StageResult(StageVerdict.Done, "Implemented REQ-1..3 in Calc.cs.", []));
        StageResultIO.Write(workspace, "feat-001", "test-runner",
            new StageResult(StageVerdict.Done, "Suite green, nothing to adjudicate.", []));

        await new RenderHandoffGate().RunAsync(
            new GateRequest(BuiltinRegistry.RenderHandoff, workspace, "feat-001"), CancellationToken.None);

        var handoff = File.ReadAllText(ArtifactPaths.HandoffPath(workspace, "feat-001"));
        Assert.Contains("Implemented REQ-1..3 in Calc.cs.", handoff);
        Assert.Contains("Suite green, nothing to adjudicate.", handoff);
        Assert.DoesNotContain("Stage completed.", handoff);
    }

    [Fact]
    public async Task TheHandoffDocument_StillRendersWithoutAnyStageResult()
    {
        var workspace = Workspace();

        await new RenderHandoffGate().RunAsync(
            new GateRequest(BuiltinRegistry.RenderHandoff, workspace, "feat-001"), CancellationToken.None);

        Assert.Contains("Stage completed.", File.ReadAllText(ArtifactPaths.HandoffPath(workspace, "feat-001")));
    }

    [Fact]
    public void WithoutAPreviousResult_ThereIsNoContext()
    {
        Assert.Equal(string.Empty, StageResultIO.BuildContext(Workspace(), "feat-001", "developer"));
        Assert.Equal(string.Empty, StageResultIO.BuildContext(Workspace(), "feat-001", null));
    }
}
