using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class NegotiationProtocolTests
{
    private static ReviewFinding Point(
        string summary = "Tests don't name the requirement",
        string? expected = "a test named REQ_1_...",
        int round = 1,
        ReviewFindingResponse response = ReviewFindingResponse.None,
        ReviewFindingStatus status = ReviewFindingStatus.Open,
        string? requirementRef = null)
        => new()
        {
            Summary = summary,
            Expected = expected,
            Round = round,
            ResponseKind = response,
            Status = status,
            RequirementRef = requirementRef,
        };

    [Fact]
    public void NextRound_StartsAtOneAndIncrementsPerPushToTheSameTarget()
    {
        Assert.Equal(1, NegotiationProtocol.NextRound([]));
        Assert.Equal(2, NegotiationProtocol.NextRound([Point(round: 1)]));
        Assert.Equal(3, NegotiationProtocol.NextRound([Point(round: 1), Point(round: 2)]));
    }

    [Fact]
    public void Decide_ContinuesWhileThereIsRoomAndNoSignal()
    {
        var action = NegotiationProtocol.Decide(1, [Point()]);

        Assert.Equal(NegotiationAction.Continue, action);
    }

    [Fact]
    public void Decide_SendsADisputeToTheUser()
    {
        var action = NegotiationProtocol.Decide(1, [Point(response: ReviewFindingResponse.Disputed)]);

        Assert.Equal(NegotiationAction.EscalateToUser, action);
    }

    [Fact]
    public void Decide_SendsABlockToTheAnalystForRescoping()
    {
        var action = NegotiationProtocol.Decide(1, [Point(response: ReviewFindingResponse.Blocked)]);

        Assert.Equal(NegotiationAction.EscalateToBa, action);
    }

    [Fact]
    public void Decide_EscalatesToTheAnalystPastTheCap()
    {
        var action = NegotiationProtocol.Decide(
            NegotiationProtocol.MaxRounds + 1, [Point()]);

        Assert.Equal(NegotiationAction.EscalateToBa, action);
    }

    [Fact]
    public void Decide_DoesNotEscalatePastTheCapWhenNothingIsOpen()
    {
        var action = NegotiationProtocol.Decide(
            NegotiationProtocol.MaxRounds + 1, []);

        Assert.Equal(NegotiationAction.Continue, action);
    }

    [Fact]
    public void BuildPointsBlock_NumbersEachPointWithItsExpectation()
    {
        var block = NegotiationProtocol.BuildPointsBlock(
        [
            Point(summary: "Tests don't name the requirement", expected: "a test named REQ_1_...", requirementRef: "REQ-1"),
            Point(summary: "Coverage of REQ-2 is missing", expected: "a test named REQ_2_..."),
        ]);

        Assert.Contains("1. [REQ-1] Tests don't name the requirement", block);
        Assert.Contains("Expected: a test named REQ_1_...", block);
        Assert.Contains("2. Coverage of REQ-2 is missing", block);
        Assert.Contains("ADDRESSED", block);
        Assert.Contains("DISPUTED", block);
        Assert.Contains("BLOCKED", block);
    }

    [Fact]
    public void BuildPointsBlock_IsEmptyWithNoOpenPoints()
    {
        Assert.Equal(string.Empty, NegotiationProtocol.BuildPointsBlock([]));
    }

    [Fact]
    public void ParseResponses_ReadsEachPointStatusAndDetail()
    {
        var points = new[] { Point(), Point(), Point() };
        var reply = """
            1. ADDRESSED — renamed tests to REQ_1_* in CoreArithmeticTests.cs
            #2: BLOCKED because there is no test project configured
            3 — DISPUTED: the check looks for REQ-3 but the requirement was merged into REQ-1
            """;

        var responses = NegotiationProtocol.ParseResponses(reply, points);

        Assert.Equal(ReviewFindingResponse.Addressed, responses[0].Kind);
        Assert.Contains("CoreArithmeticTests.cs", responses[0].Detail);
        Assert.Equal(ReviewFindingResponse.Blocked, responses[1].Kind);
        Assert.Equal(ReviewFindingResponse.Disputed, responses[2].Kind);
    }

    [Fact]
    public void ParseResponses_LeavesUnmentionedPointsUnanswered()
    {
        var points = new[] { Point(), Point() };

        var responses = NegotiationProtocol.ParseResponses("1. ADDRESSED — done", points);

        Assert.Equal(ReviewFindingResponse.Addressed, responses[0].Kind);
        Assert.Equal(ReviewFindingResponse.None, responses[1].Kind);
    }

    [Fact]
    public void ParseResponses_DoesNotMistakeProseForAnAnswer()
    {
        var points = new[] { Point() };

        var responses = NegotiationProtocol.ParseResponses(
            "I looked at the feedback and it seems already addressed elsewhere.", points);

        Assert.Equal(ReviewFindingResponse.None, responses[0].Kind);
    }
}
