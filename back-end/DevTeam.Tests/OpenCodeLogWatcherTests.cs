using DevTeam.Broker.Models;

namespace DevTeam.Tests;

public class OpenCodeLogWatcherTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "dt-opencode-" + Guid.NewGuid().ToString("N") + ".log");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    [Fact]
    public async Task NoticesAStreamErrorForThisSession()
    {
        File.WriteAllText(_path, "level=INFO message=loop session.id=other step=1\n");
        using var watcher = new OpenCodeLogWatcher(_path, TimeSpan.FromMilliseconds(50));
        var seen = new TaskCompletionSource<ProviderFailure>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var scope = watcher.Watch("ses_mine", failure => seen.TrySetResult(failure));

        await Task.Delay(150); // let it record where "now" starts
        File.AppendAllText(_path, "level=INFO message=loop session.id=other step=2\n");
        File.AppendAllText(_path,
            "level=ERROR message=\"stream error\" session.id=ses_mine error.error=\"AI_APICallError: Rate limit exceeded. Please try again later.\"\n");

        var failure = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderFailureKind.RateLimited, failure.Kind);
    }

    [Fact]
    public async Task IgnoresStreamErrorsForOtherSessions()
    {
        File.WriteAllText(_path, string.Empty);
        using var watcher = new OpenCodeLogWatcher(_path, TimeSpan.FromMilliseconds(50));
        var fired = false;

        using var scope = watcher.Watch("ses_mine", _ => fired = true);

        await Task.Delay(150);
        File.AppendAllText(_path,
            "level=ERROR message=\"stream error\" session.id=ses_other error.error=\"AI_APICallError: Rate limit exceeded.\"\n");
        await Task.Delay(300);

        Assert.False(fired);
    }

    [Fact]
    public async Task IgnoresFailuresThatHappenedBeforeTheTurnStarted()
    {
        // A failure from an earlier run must never abort this one.
        File.WriteAllText(_path,
            "level=ERROR message=\"stream error\" session.id=ses_mine error.error=\"AI_APICallError: Rate limit exceeded.\"\n");
        using var watcher = new OpenCodeLogWatcher(_path, TimeSpan.FromMilliseconds(50));
        var fired = false;

        using var scope = watcher.Watch("ses_mine", _ => fired = true);
        await Task.Delay(300);

        Assert.False(fired);
    }
}
