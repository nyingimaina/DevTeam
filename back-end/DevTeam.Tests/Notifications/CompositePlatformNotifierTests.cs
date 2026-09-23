using DevTeam.Broker.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests.Notifications;

public class CompositePlatformNotifierTests
{
    private sealed class RecordingNotifier : IPlatformNotifier
    {
        public List<NotificationRequest> Requests { get; } = [];
        public Exception? FailWith { get; set; }

        public Task NotifyAsync(NotificationRequest request, CancellationToken ct)
        {
            if (FailWith is not null)
                throw FailWith;

            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NotifyAsync_FansOutToEveryRegisteredNotifier()
    {
        var toast = new RecordingNotifier();
        var semaNami = new RecordingNotifier();
        var composite = new CompositePlatformNotifier([toast, semaNami], NullLogger<CompositePlatformNotifier>.Instance);
        var request = new NotificationRequest("Title", "Message");

        await composite.NotifyAsync(request, CancellationToken.None);

        Assert.Same(request, Assert.Single(toast.Requests));
        Assert.Same(request, Assert.Single(semaNami.Requests));
    }

    [Fact]
    public async Task NotifyAsync_OneNotifierFailing_StillReachesTheOthers()
    {
        var failing = new RecordingNotifier { FailWith = new InvalidOperationException("network down") };
        var working = new RecordingNotifier();
        var composite = new CompositePlatformNotifier([failing, working], NullLogger<CompositePlatformNotifier>.Instance);

        await composite.NotifyAsync(new NotificationRequest("Title", "Message"), CancellationToken.None);

        Assert.Single(working.Requests);
    }

    [Fact]
    public async Task NotifyAsync_AllNotifiersFailing_DoesNotThrow()
    {
        var failing = new RecordingNotifier { FailWith = new InvalidOperationException("network down") };
        var composite = new CompositePlatformNotifier([failing], NullLogger<CompositePlatformNotifier>.Instance);

        var exception = await Record.ExceptionAsync(
            () => composite.NotifyAsync(new NotificationRequest("Title", "Message"), CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task NotifyAsync_NoNotifiersRegistered_DoesNotThrow()
    {
        var composite = new CompositePlatformNotifier([], NullLogger<CompositePlatformNotifier>.Instance);

        var exception = await Record.ExceptionAsync(
            () => composite.NotifyAsync(new NotificationRequest("Title", "Message"), CancellationToken.None));

        Assert.Null(exception);
    }
}
