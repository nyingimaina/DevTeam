using DevTeam.Broker.Server;

namespace DevTeam.Tests;

/// <summary>
/// The context bar is only worth having if the number behind it is honest, and the bar being full is
/// only actionable if something sensible happens next. These tests pin the policy: when to compact,
/// when to refuse, and what to write down when refusing.
/// </summary>
public class ContextCompactionPolicyTests
{
    private static ContextDto Reading(long used, long? size = 200_000, bool turnActive = false)
        => new(null, used, size, null, null, null, turnActive, 0, DateTimeOffset.UtcNow);

    [Fact]
    public void AContextUnderTheThresholdIsLeftAlone()
    {
        var decision = ContextCompactionPolicy.Decide(
            new ContextCompactionPolicy(), Reading(100_000), turnActive: false);

        Assert.False(decision.ShouldCompact);
        Assert.Equal("below the threshold", decision.SkipReason);
    }

    [Fact]
    public void AFullContextIsCompacted()
    {
        var decision = ContextCompactionPolicy.Decide(
            new ContextCompactionPolicy(), Reading(160_000), turnActive: false);

        Assert.True(decision.ShouldCompact);
        Assert.Equal(0.8, decision.Occupancy!.Value, 3);
    }

    [Fact]
    public void AMidTurnContextIsNeverCompacted()
    {
        // Compacting during a turn would change the context out from under it, and the reading on the
        // bar would describe a context that no longer exists.
        var decision = ContextCompactionPolicy.Decide(
            new ContextCompactionPolicy(), Reading(199_000, turnActive: true), turnActive: true);

        Assert.False(decision.ShouldCompact);
        Assert.Equal("a turn is in progress", decision.SkipReason);
    }

    [Fact]
    public void AContextTheAgentNeverMeasuredIsNotCompactedOnAGuess()
    {
        // Spending a model call to compact a context of unknown size is a guess, not a decision.
        var decision = ContextCompactionPolicy.Decide(
            new ContextCompactionPolicy(),
            new ContextDto(null, 150_000, null, null, null, null, false, 0, null),
            turnActive: false);

        Assert.False(decision.ShouldCompact);
        Assert.Equal("the agent has not reported a context reading yet", decision.SkipReason);
    }

    [Fact]
    public void AZeroContextSizeIsTreatedAsUnknownRatherThanAsAFullWindow()
    {
        var decision = ContextCompactionPolicy.Decide(
            new ContextCompactionPolicy(), Reading(150_000, size: 0), turnActive: false);

        Assert.False(decision.ShouldCompact);
        Assert.Equal("the agent has not reported a context size yet", decision.SkipReason);
    }

    [Fact]
    public void AContextThatStayedFullAfterCompactingIsNotCompactedAgainImmediately()
    {
        // The loop this guards against: compaction frees little, the context is still over the line,
        // and the next check fires again. Every one of those is a paid model call for nothing.
        var policy = new ContextCompactionPolicy().AfterCompaction(usedTokensAfter: 170_000);

        var decision = ContextCompactionPolicy.Decide(policy, Reading(175_000), turnActive: false);

        Assert.False(decision.ShouldCompact);
        Assert.Contains("already compacted", decision.SkipReason);
    }

    [Fact]
    public void OnceTheContextHasGrownWellPastWhereCompactionLeftItAnotherIsAllowed()
    {
        var policy = new ContextCompactionPolicy().AfterCompaction(usedTokensAfter: 60_000);

        var decision = ContextCompactionPolicy.Decide(policy, Reading(180_000), turnActive: false);

        Assert.True(decision.ShouldCompact);
    }

    [Fact]
    public void TheThresholdIsConfigurable()
    {
        var eager = new ContextCompactionPolicy { Threshold = 0.50 };

        var decision = ContextCompactionPolicy.Decide(eager, Reading(110_000), turnActive: false);

        Assert.True(decision.ShouldCompact);
    }
}
