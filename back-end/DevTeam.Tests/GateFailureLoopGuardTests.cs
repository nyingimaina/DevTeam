using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

public class GateFailureLoopGuardTests
{
    private static ReleaseStageRun Run() => new() { StageName = "developer" };

    [Fact]
    public void RecordFailure_FirstTimeCountsOneAndDoesNotSuppress()
    {
        var run = Run();

        GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);

        Assert.Equal(1, run.ConsecutiveFailures);
        Assert.False(run.AutoRetrySuppressed);
    }

    [Fact]
    public void RecordFailure_SameChecksIncrement()
    {
        var run = Run();

        GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);
        GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);

        Assert.Equal(2, run.ConsecutiveFailures);
        Assert.False(run.AutoRetrySuppressed);
    }

    [Fact]
    public void RecordFailure_SuppressesAtTheCap()
    {
        var run = Run();

        for (var i = 0; i < GateFailureLoopGuard.MaxConsecutiveFailures; i++)
            GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);

        Assert.Equal(GateFailureLoopGuard.MaxConsecutiveFailures, run.ConsecutiveFailures);
        Assert.True(run.AutoRetrySuppressed);
    }

    [Fact]
    public void RecordFailure_ResetsWhenADifferentCheckFails()
    {
        var run = Run();

        GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);
        GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);
        GateFailureLoopGuard.RecordFailure(run, ["reuse_gate"]);

        Assert.Equal(1, run.ConsecutiveFailures);
        Assert.False(run.AutoRetrySuppressed);
    }

    [Fact]
    public void RecordFailure_OrderIndependentSignature()
    {
        var run = Run();

        GateFailureLoopGuard.RecordFailure(run, ["a_gate", "b_gate"]);
        GateFailureLoopGuard.RecordFailure(run, ["b_gate", "a_gate"]);

        Assert.Equal(2, run.ConsecutiveFailures);
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var run = Run();
        for (var i = 0; i < GateFailureLoopGuard.MaxConsecutiveFailures; i++)
            GateFailureLoopGuard.RecordFailure(run, ["code_hygiene"]);

        GateFailureLoopGuard.Clear(run);

        Assert.Equal(0, run.ConsecutiveFailures);
        Assert.Null(run.LastFailureSignature);
        Assert.False(run.AutoRetrySuppressed);
    }
}
