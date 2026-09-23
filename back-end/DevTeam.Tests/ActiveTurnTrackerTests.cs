using DevTeam.Broker.Server;
using Xunit;

namespace DevTeam.Tests;

public class ActiveTurnTrackerTests
{
    [Fact]
    public void Current_WithNoActiveTurn_IsNull()
    {
        var tracker = new ActiveTurnTracker();
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void Begin_SetsCurrentTurnInfo()
    {
        var tracker = new ActiveTurnTracker();
        var sessionId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        using var scope = tracker.Begin(sessionId, "acp-1", "hello world", cts);

        Assert.NotNull(tracker.Current);
        Assert.Equal(sessionId, tracker.Current!.SessionId);
        Assert.Equal("hello world", tracker.Current.Preview);
    }

    [Fact]
    public void Begin_ExposesTheStageRunSessionIdTheUiCorrelatesOn()
    {
        // ReleaseStageRun.AcpSessionId actually stores the DevTeamSession id, so the UI must
        // compare against StageRunSessionId — not the real ACP id.
        var tracker = new ActiveTurnTracker();
        var sessionId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();

        using var scope = tracker.Begin(sessionId, "real-acp-id", "hello", cts);

        Assert.Equal(sessionId, tracker.Current!.StageRunSessionId);
        Assert.NotEqual(tracker.Current.AcpSessionId, tracker.Current.StageRunSessionId.ToString());
    }

    [Fact]
    public void Begin_RemembersWhetherThePromptWasComposed()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();

        using var scope = tracker.Begin(Guid.NewGuid(), "acp-1", "composed prompt", cts, isPriming: true);

        Assert.True(tracker.Current!.IsPriming);
    }

    [Fact]
    public void DisposingScope_ClearsCurrentTurn()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();

        var scope = tracker.Begin(Guid.NewGuid(), "acp-1", "hello", cts);
        scope.Dispose();

        Assert.Null(tracker.Current);
    }

    [Fact]
    public void TryCancelCurrent_WithNoActiveTurn_ReturnsFalse()
    {
        var tracker = new ActiveTurnTracker();
        Assert.False(tracker.TryCancelCurrent(out _));
    }

    [Fact]
    public void TryCancelCurrent_CancelsTheTrackedTokenSource()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        using var scope = tracker.Begin(Guid.NewGuid(), "acp-1", "hello", cts);

        var cancelled = tracker.TryCancelCurrent(out var info);

        Assert.True(cancelled);
        Assert.NotNull(info);
        Assert.Equal("acp-1", info!.AcpSessionId);
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void Record_AppendsActivityAndRefreshesTheHeartbeat()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        using var scope = tracker.Begin(Guid.NewGuid(), "acp-1", "hello", cts);

        // Begin records a start entry, so the heartbeat is set from the very first moment.
        Assert.NotNull(tracker.Current!.LastEventAt);
        Assert.Single(tracker.Current.Activity!);
        Assert.Equal(TurnActivityKind.Status, tracker.Current.Activity![0].Kind);

        tracker.Record(TurnActivityKind.Tool, "Read foo.cs", status: "running");

        var info = tracker.Current!;
        Assert.Equal(2, info.Activity!.Count);
        Assert.Equal("Read foo.cs", info.Activity[1].Label);
        Assert.Equal("running", info.Activity[1].Status);
    }

    [Fact]
    public void Record_KeepsOnlyTheMostRecentEntries()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        using var scope = tracker.Begin(Guid.NewGuid(), "acp-1", "hello", cts);

        for (var i = 0; i < ActiveTurnTracker.ActivityCapacity + 20; i++)
            tracker.Record(TurnActivityKind.Text, $"line {i}");

        var activity = tracker.Current!.Activity!;
        Assert.Equal(ActiveTurnTracker.ActivityCapacity, activity.Count);
        Assert.Equal($"line {ActiveTurnTracker.ActivityCapacity + 19}", activity[^1].Label);
    }

    [Fact]
    public void BeginQueued_IsVisibleAsAQueueLength()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        using var running = tracker.Begin(Guid.NewGuid(), "acp-1", "running", cts);

        var firstInLine = tracker.BeginQueued();
        Assert.Equal(1, tracker.Current!.QueuedTurns);

        var secondInLine = tracker.BeginQueued();
        Assert.Equal(2, tracker.Current.QueuedTurns);

        // Acquiring the slot takes the caller out of the queue.
        firstInLine.Dispose();
        Assert.Equal(1, tracker.Current.QueuedTurns);

        secondInLine.Dispose();
        Assert.Equal(0, tracker.Current.QueuedTurns);
    }

    [Fact]
    public void Begin_StartsAFreshActivityFeed()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        var first = tracker.Begin(Guid.NewGuid(), "acp-1", "one", cts);
        tracker.Record(TurnActivityKind.Tool, "Read foo.cs");
        first.Dispose();

        using var second = tracker.Begin(Guid.NewGuid(), "acp-2", "two", cts);

        Assert.Single(tracker.Current!.Activity!);
    }

    [Fact]
    public void Record_OutsideATurn_IsIgnored()
    {
        var tracker = new ActiveTurnTracker();

        tracker.Record(TurnActivityKind.Text, "nothing to attach this to");

        Assert.Null(tracker.Current);
    }

    [Fact]
    public void PreviewIsTruncated()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        var longText = new string('x', 200);

        using var scope = tracker.Begin(Guid.NewGuid(), "acp-1", longText, cts);

        Assert.True(tracker.Current!.Preview.Length <= 80);
    }
}
