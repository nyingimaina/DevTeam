using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class ReviewVerdictTests
{
    [Fact]
    public void Pass_IsRecognised()
    {
        var verdict = ReviewVerdict.Parse("Looked at the diff; all in scope.\nVERDICT: PASS");

        Assert.Equal(ReviewOutcome.Pass, verdict.Outcome);
    }

    [Theory]
    [InlineData("VERDICT: FAIL - Foo.cs duplicates Core/Bar.cs")]
    [InlineData("verdict: fail — Foo.cs duplicates Core/Bar.cs")]
    [InlineData("**VERDICT: FAIL** - Foo.cs duplicates Core/Bar.cs")]
    [InlineData("`VERDICT: FAIL: Foo.cs duplicates Core/Bar.cs`")]
    public void Fail_CarriesTheReviewersSentence(string line)
    {
        var verdict = ReviewVerdict.Parse("Reasoning...\n" + line);

        Assert.Equal(ReviewOutcome.Fail, verdict.Outcome);
        Assert.StartsWith("Foo.cs duplicates Core/Bar.cs", verdict.Detail);
    }

    [Fact]
    public void TheLastVerdictLineWins_SoQuotingTheFormatWhileReasoningIsHarmless()
    {
        var verdict = ReviewVerdict.Parse(
            "I will end with a line like:\nVERDICT: PASS\nbut first, a problem.\nVERDICT: FAIL - the migration is missing");

        Assert.Equal(ReviewOutcome.Fail, verdict.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Everything looks fine to me.")]
    [InlineData("My verdict: pass, I think")]
    [InlineData("The VERDICT: PASS line is required")]
    public void WithoutAVerdictLine_TheOutcomeIsMissing_NeverAnImplicitPass(string? reply)
    {
        Assert.Equal(ReviewOutcome.Missing, ReviewVerdict.Parse(reply).Outcome);
    }
}
