using DevTeam.Broker.Domain;
using DevTeam.Broker.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class UserNotifierTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CapturingPlatformNotifier _platform = new();

    public UserNotifierTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task AFinishedStage_Notifies()
    {
        await CreateNotifier().StageFinishedAsync(Outcome("Complete", allPassed: true), CancellationToken.None);

        var request = Assert.Single(_platform.Requests);
        Assert.Contains("Developer finished", request.Title);
        Assert.Contains("'subtraction'", request.Message);
        Assert.Equal(NotificationUrgency.Info, request.Urgency);
    }

    [Fact]
    public async Task AwaitingApproval_NotifiesWithTheAttentionSound()
    {
        await CreateNotifier().StageFinishedAsync(Outcome("BlockedSignoff", allPassed: true), CancellationToken.None);

        var request = Assert.Single(_platform.Requests);
        Assert.Contains("waiting for you", request.Title);
        Assert.Equal(NotificationUrgency.Attention, request.Urgency);
    }

    [Theory]
    [InlineData("BlockedGate")]
    [InlineData("BlockedEntry")]
    [InlineData("Escalated")]
    public async Task AStuckStage_NotifiesWithTheAttentionSound(string status)
    {
        await CreateNotifier().StageFinishedAsync(Outcome(status, allPassed: false), CancellationToken.None);

        var request = Assert.Single(_platform.Requests);
        Assert.Contains("needs attention", request.Title);
        Assert.Equal(NotificationUrgency.Attention, request.Urgency);
    }

    [Fact]
    public async Task AStageStillRunning_IsNotWorthANotification()
    {
        foreach (var status in new[] { "Active", "Pending", "GatesRunning", "Stale" })
            await CreateNotifier().StageFinishedAsync(Outcome(status, allPassed: false), CancellationToken.None);

        Assert.Empty(_platform.Requests);
    }

    [Fact]
    public async Task TheSameOutcomeIsOnlyAnnouncedOnce()
    {
        // Terminal states get re-reported while the UI polls; without this a blocked stage would
        // fire a notification on every poll.
        var notifier = CreateNotifier();

        await notifier.StageFinishedAsync(Outcome("BlockedGate", allPassed: false), CancellationToken.None);
        await notifier.StageFinishedAsync(Outcome("BlockedGate", allPassed: false), CancellationToken.None);

        Assert.Single(_platform.Requests);
    }

    [Fact]
    public async Task DifferentStagesStillNotifyIndependently()
    {
        var notifier = CreateNotifier();

        await notifier.StageFinishedAsync(Outcome("Complete", allPassed: true), CancellationToken.None);
        await notifier.StageFinishedAsync(Outcome("Complete", allPassed: true, stage: "qa"), CancellationToken.None);

        Assert.Equal(2, _platform.Requests.Count);
    }

    [Fact]
    public async Task PreferencesCanSilenceAnEvent()
    {
        var settings = await CreateSettingsAsync();
        await settings.SetAsync(stageComplete: false, needsAttention: true, approvalNeeded: true, sound: true, CancellationToken.None);

        var notifier = new UserNotifier(_platform, settings, NullLogger<UserNotifier>.Instance);
        await notifier.StageFinishedAsync(Outcome("Complete", allPassed: true), CancellationToken.None);

        Assert.Empty(_platform.Requests);
    }

    [Fact]
    public async Task TurningSoundOffDropsTheAlarmButKeepsTheNotification()
    {
        var settings = await CreateSettingsAsync();
        await settings.SetAsync(stageComplete: true, needsAttention: true, approvalNeeded: true, sound: false, CancellationToken.None);

        var notifier = new UserNotifier(_platform, settings, NullLogger<UserNotifier>.Instance);
        await notifier.StageFinishedAsync(Outcome("BlockedGate", allPassed: false), CancellationToken.None);

        var request = Assert.Single(_platform.Requests);
        Assert.Equal(NotificationUrgency.Info, request.Urgency);
    }

    [Fact]
    public async Task AFailingPlatformNotifier_DoesNotThrow()
    {
        _platform.FailWith = new InvalidOperationException("powershell missing");

        await CreateNotifier().StageFinishedAsync(Outcome("Complete", allPassed: true), CancellationToken.None);
    }

    [Fact]
    public async Task FeatureIdFlowsThroughToTheNotificationRequest()
    {
        // A platform notifier that needs to bind a reply channel to the right feature (e.g.
        // SemaNamiPlatformNotifier) reads this — the outcome's own FeatureId is the reliable
        // signal, not something worth re-deriving from Title/Message text.
        var featureId = Guid.NewGuid();

        await CreateNotifier().StageFinishedAsync(Outcome("BlockedSignoff", allPassed: true, featureId: featureId), CancellationToken.None);

        var request = Assert.Single(_platform.Requests);
        Assert.Equal(featureId, request.FeatureId);
    }

    private static StageOutcome Outcome(string status, bool allPassed, string stage = "developer", Guid? featureId = null)
        => new("subtraction", stage, status, allPassed, featureId);

    private UserNotifier CreateNotifier() => new(_platform, CreateSettingsAsync().GetAwaiter().GetResult(), NullLogger<UserNotifier>.Instance);

    private Task<NotificationSettings> CreateSettingsAsync() => Task.FromResult(new NotificationSettings(CreateFactory()));

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class CapturingPlatformNotifier : IPlatformNotifier
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

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}
