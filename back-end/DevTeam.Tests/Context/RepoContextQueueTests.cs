using DevTeam.Broker.Context;

namespace DevTeam.Tests.Context;

public class RepoContextQueueTests
{
    [Fact]
    public async Task Enqueue_CoalescesRepeatedRequestsForTheSameWorkspace()
    {
        var queue = new RepoContextQueue();

        queue.Enqueue(@"C:\work\proj", "feature-complete");
        queue.Enqueue(@"C:\work\proj", "feature-complete");
        queue.Enqueue(@"C:\work\proj", "hotfix-complete");

        Assert.Equal(1, queue.PendingCount);
        var work = await queue.DequeueAsync(CancellationToken.None);
        Assert.NotNull(work);
        Assert.Equal(@"C:\work\proj", work!.WorkspacePath);
        // Coalescing keeps the last trigger.
        Assert.Equal("hotfix-complete", work.Trigger);
        Assert.Null(await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public void DifferentWorkspaces_DoNotCollapse()
    {
        var queue = new RepoContextQueue();

        queue.Enqueue(@"C:\a", "feature-complete");
        queue.Enqueue(@"C:\b", "feature-complete");

        Assert.Equal(2, queue.PendingCount);
        Assert.True(queue.IsPending(@"C:\a"));
        Assert.True(queue.IsPending(@"C:\b"));
    }

    [Fact]
    public void Enqueue_IgnoresABlankWorkspace()
    {
        var queue = new RepoContextQueue();

        queue.Enqueue("  ", "feature-complete");

        Assert.Equal(0, queue.PendingCount);
    }
}
