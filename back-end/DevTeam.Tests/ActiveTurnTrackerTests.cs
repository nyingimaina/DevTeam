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
    public void PreviewIsTruncated()
    {
        var tracker = new ActiveTurnTracker();
        using var cts = new CancellationTokenSource();
        var longText = new string('x', 200);

        using var scope = tracker.Begin(Guid.NewGuid(), "acp-1", longText, cts);

        Assert.True(tracker.Current!.Preview.Length <= 80);
    }
}
