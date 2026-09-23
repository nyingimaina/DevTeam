using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class InterviewLogTests
{
    [Fact]
    public void Append_NumbersQuestionsInOrder()
    {
        var log = InterviewLog.Append(null, "What should it do?", "Add two numbers");

        Assert.Equal(1, InterviewLog.CountQuestions(log));
        Assert.Contains("## Q1", log);
        Assert.Contains("What should it do?", log);
        Assert.Contains("**Answer:** Add two numbers", log);
    }

    [Fact]
    public void Append_AddsSubsequentTurnsWithoutLosingEarlierOnes()
    {
        var log = InterviewLog.Append(null, "Q one?", "A one");
        log = InterviewLog.Append(log, "Q two?", "A two");

        Assert.Equal(2, InterviewLog.CountQuestions(log));
        Assert.Contains("## Q1", log);
        Assert.Contains("## Q2", log);
        Assert.Contains("A one", log);
        Assert.Contains("A two", log);
    }

    [Fact]
    public void Append_TreatsAnEmptyAnswerAsNoAnswer()
    {
        var log = InterviewLog.Append(null, "Anything?", "");

        Assert.Contains("(no answer)", log);
    }

    [Fact]
    public void CountQuestions_IsZeroForEmptyOrNull()
    {
        Assert.Equal(0, InterviewLog.CountQuestions(null));
        Assert.Equal(0, InterviewLog.CountQuestions(""));
    }
}
