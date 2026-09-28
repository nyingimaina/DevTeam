using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;

namespace DevTeam.Tests;

/// <summary>
/// The context belongs to the session, so the state that holds it has to outlive the turn that
/// produced it, and it has to be able to notice a reading that arrives a moment after the turn
/// already finished. These tests cover that state on its own, because whether the notification
/// beats the prompt response is a genuine race in production and cannot be forced deterministically
/// from outside the coordinator without injecting timing hooks into it.
/// </summary>
public class SessionContextStateTests
{
    [Fact]
    public void TheLastReadingIsStillThereAfterTheTurnThatProducedItEnds()
    {
        var state = new SessionContextState();
        state.BeginTurn();
        state.NoteUsage("ses_abc", 130_000, 200_000, 1.25m, "USD", "feature-alpha");
        state.EndTurn();

        Assert.Equal(130_000, state.UsedTokens);
        Assert.Equal(200_000, state.ContextSize);
        Assert.Equal(1.25m, state.CostAmount);
        Assert.Equal("USD", state.CostCurrency);
        Assert.Equal("feature-alpha", state.FeatureKey);
        Assert.False(state.TurnActive);
    }

    [Fact]
    public void ARememberedReadingKnowsTheTurnItCameFromHasFinished()
    {
        var state = new SessionContextState();
        state.BeginTurn();
        Assert.True(state.TurnActive);

        state.EndTurn();

        Assert.False(state.TurnActive);
    }

    [Fact]
    public void EachReadingCanSayHowMuchContextTheOneBeforeItFreed()
    {
        // The delta is what makes a context bar readable: 130k after 12k is growth, 21k after 130k
        // is a compaction that worked. Reporting only the absolute number leaves the reader guessing.
        var state = new SessionContextState();
        state.NoteUsage("ses_abc", 12_000, 200_000, null, null, null);
        state.NoteUsage("ses_abc", 130_000, 200_000, null, null, null);

        Assert.Equal(118_000, state.LastDeltaTokens);

        state.NoteUsage("ses_abc", 21_000, 200_000, null, null, null);

        Assert.Equal(-109_000, state.LastDeltaTokens);
    }

    [Fact]
    public void TheFirstReadingHasNoDeltaToReport()
    {
        var state = new SessionContextState();
        state.NoteUsage("ses_abc", 12_000, 200_000, null, null, null);

        Assert.Null(state.LastDeltaTokens);
    }

    [Fact]
    public async Task AReadingThatLandsWhileTheTurnIsStillBeingWaitedOnIsCaught()
    {
        // The common case: the agent reports context a few milliseconds after the prompt returns,
        // which is after the reply has been read but before the turn has been torn down.
        var state = new SessionContextState();
        state.BeginTurn();

        var waiter = state.WaitForUsageAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        _ = Task.Run(async () =>
        {
            await Task.Delay(30);
            state.NoteUsage("ses_abc", 77_000, 200_000, null, null, null);
        });

        var usage = await waiter;

        Assert.NotNull(usage);
        Assert.Equal(77_000, usage.UsedTokens);
    }

    [Fact]
    public async Task AReadingThatAlreadyLandedIsNotWaitedForAgain()
    {
        // The other ordering. Waiting unconditionally for a notification that has already arrived
        // would cost every turn the full grace period.
        var state = new SessionContextState();
        state.BeginTurn();
        state.NoteUsage("ses_abc", 55_000, 200_000, null, null, null);

        var started = DateTime.UtcNow;
        var usage = await state.WaitForUsageAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(55_000, usage.UsedTokens);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1), "an already-landed reading must return immediately");
    }

    [Fact]
    public async Task ATurnThatNeverReportsContextGivesUpInsteadOfHanging()
    {
        // Errored, cancelled and slash-handled turns never report usage. Without an expiry the turn
        // would wait out the full grace period on every one of them.
        var state = new SessionContextState();
        state.BeginTurn();

        var started = DateTime.UtcNow;
        var usage = await state.WaitForUsageAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;

        Assert.Null(usage);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"the wait must expire on its own, took {elapsed}");
    }

    [Fact]
    public async Task AReadingFromAnEarlierTurnIsNotMistakenForThisTurns()
    {
        // Each turn has to wait only for its own notification. If the previous turn's reading were
        // reused, a turn that reports nothing would be credited with the last turn's context and
        // would look like it was working against a full window.
        var state = new SessionContextState();
        state.BeginTurn();
        state.NoteUsage("ses_abc", 130_000, 200_000, null, null, null);
        state.EndTurn();

        state.BeginTurn();
        var usage = await state.WaitForUsageAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Null(usage);
    }

    [Fact]
    public void ClearingTheContextRecordsThatItHappenedAndCountsIt()
    {
        var state = new SessionContextState();
        state.NoteUsage("ses_abc", 120_000, 200_000, null, null, "feature-alpha");

        state.Clear("ses_new", ContextSampleSource.BoundaryReset, "feature-beta");

        Assert.Equal(0, state.UsedTokens);
        Assert.Equal(1, state.CompactionCount);
        Assert.Equal("ses_new", state.AcpSessionId);
        Assert.Equal("feature-beta", state.FeatureKey);
    }

    [Fact]
    public void TheTurnIsNotLeftMarkedActiveIfItEndsWithoutOne()
    {
        var state = new SessionContextState();
        state.BeginTurn();

        state.EndTurn();

        Assert.False(state.TurnActive);
    }
}
