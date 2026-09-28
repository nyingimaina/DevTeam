using System.Net;
using DevTeam.Broker;
using DevTeam.Broker.Domain;
using DevTeam.Broker.SemaNami;
using DevTeam.Broker.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DevTeam.Tests;

// The recoverer itself is verified by WorkflowCrashRecovererTests; until now nothing in the
// broker ever RAN it — the only production reference was a comment. That gap left a real
// incident unhealable: a BA run sat in GatesRunning for over 90 live minutes (its challenge
// turn finished at 09:53:35, the gate flow never resumed), and the documented "healed on
// restart" never happened because restart invoked nothing. This file pins the wiring: a run
// wedged before the host boots must come back runnable through the broker's real startup path.
public class WorkflowCrashRecoveryStartupTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "devteam-recovery-" + Guid.NewGuid().ToString("N") + ".db");

    private RecoveryAppFactory? _factory;

    [Fact]
    public async Task Broker_Startup_HealsAGateRunWedgedInTheGates()
    {
        var wedged = SeedRun(
            ReleaseStageStatus.GatesRunning, StagePhase.Gates, checkpointSignal: StageCheckpointSignal.PromptInProgress);
        Assert.Equal(ReleaseStageStatus.GatesRunning, ReadRun(wedged).Status);

        _factory = new RecoveryAppFactory(_dbPath);
        var client = _factory.CreateClient();
        (await client.GetAsync("/healthz")).EnsureSuccessStatusCode();

        await using var db = OpenDb();
        var run = db.ReleaseStageRuns.Include(r => r.GuidanceNotes).Single(r => r.ReleaseFeatureId == wedged);
        Assert.Equal(ReleaseStageStatus.Active, run.Status);
        Assert.Equal(StagePhase.Gates, run.Phase);
        Assert.False(run.ReadyToProceed);
        var note = Assert.Single(run.GuidanceNotes);
        Assert.Equal("system", note.AddedBy);
        Assert.Contains("interrupted", note.Text);
    }

    [Fact]
    public async Task Broker_Startup_EscalatesActiveRunsWhoLostTheirTurnMidPrompt()
    {
        var lost = SeedRun(
            ReleaseStageStatus.Active,
            StagePhase.GuidedQA,
            checkpointSignal: StageCheckpointSignal.PromptInProgress);
        Assert.Equal(ReleaseStageStatus.Active, ReadRun(lost).Status);

        _factory = new RecoveryAppFactory(_dbPath);
        _factory.CreateClient();

        await using var db = OpenDb();
        var run = db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == lost);
        Assert.Equal(ReleaseStageStatus.Escalated, run.Status);
        Assert.Equal(StageErrorKind.Disconnected, run.LastErrorKind);
    }

    [Fact]
    public void Broker_Startup_RegistersTheRecoveryPassExactlyOnce()
    {
        _factory = new RecoveryAppFactory(_dbPath);
        _factory.CreateClient();

        var hosted = _factory.Services.GetServices<IHostedService>();
        Assert.Single(hosted.OfType<WorkflowCrashRecoveryService>());
    }

    [Fact]
    public async Task Broker_Startup_SkipsHealingWhenAnotherLiveBrokerOwnsTheDatabase()
    {
        // The loop this must break (observed 2026-09-28, 19:15:30 and 19:18:02): a test host
        // builds Program with the DEFAULT connection string — the production devteam.db — and
        // its boot-time recovery pass escalates the live app's mid-prompt run ("broker
        // restarted"), which tears down a turn that was alive the whole time. Ownership proves
        // the difference: a fresh heartbeat from a live pid means somebody else owns this db,
        // and healing it would be lying about a restart nobody had.
        var wedgedAsFarAsTheDbKnows = SeedRun(
            ReleaseStageStatus.Active, StagePhase.GuidedQA, checkpointSignal: StageCheckpointSignal.PromptInProgress);

        using (var db = OpenDb())
        {
            db.AppSettings.Add(new AppSetting
            {
                Name = "devteam.broker.owner",
                // Our own pid via a truthy aliveness seam: the recoverer's liveness fn says YES
                // for any pid in this scenario, but it must also differ from our own.
                Value = $"{{\"pid\":{Environment.ProcessId - 1},\"beat\":\"{DateTimeOffset.UtcNow:O}\"}}",
            });
            db.SaveChanges();
        }

        _factory = new RecoveryAppFactory(_dbPath, pidIsAlive: () => true);
        _factory.CreateClient();

        Assert.Equal(ReleaseStageStatus.Active, ReadRun(wedgedAsFarAsTheDbKnows).Status);
    }

    [Fact]
    public void Broker_Startup_HealsWhenTheOwnerHeartbeatIsStaleAndItsPidIsGone()
    {
        // The true-restart path: the recorded owner process no longer exists and its beat is
        // old, so a wedged run is genuinely ours to heal.
        var abandoned = SeedRun(
            ReleaseStageStatus.Active, StagePhase.GuidedQA, checkpointSignal: StageCheckpointSignal.PromptInProgress);

        using (var db = OpenDb())
        {
            db.AppSettings.Add(new AppSetting
            {
                Name = "devteam.broker.owner",
                Value = $"{{\"pid\":999999,\"beat\":\"{DateTimeOffset.UtcNow.AddMinutes(-10):O}\"}}",
            });
            db.SaveChanges();
        }

        _factory = new RecoveryAppFactory(_dbPath, pidIsAlive: () => false);
        _factory.CreateClient();

        Assert.Equal(ReleaseStageStatus.Escalated, ReadRun(abandoned).Status);
    }

    public void Dispose()
    {
        if (_factory is not null)
            _factory.Dispose();
        // Microsoft.Data.Sqlite pools handles, so a pooled open handle would otherwise keep the
        // file locked past the app's disposal and every cleanup would retry forever.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (File.Exists(_dbPath)) File.Delete(_dbPath);
                return;
            }
            catch when (i < 4) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(100); }
        }
    }

    private Guid SeedRun(
        ReleaseStageStatus status, StagePhase phase, StageCheckpointSignal? checkpointSignal = null)
    {
        using var db = new DevTeamDbContext(
            new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite($"Data Source={_dbPath}").Options);
        db.Database.EnsureCreated();

        var release = new DevTeamRelease { WorkspacePath = @"C:\work\proj" };
        var feature = new ReleaseFeature
        {
            Release = release,
            Key = "wedged-feature",
            Title = "Wedged feature",
            Status = ReleaseFeatureStatus.InProgress,
        };
        feature.FlowPosition = new ReleaseFlowPosition
        {
            Feature = feature,
            CurrentStageIndex = 0,
            CurrentStageName = "business-analyst",
        };
        var run = new ReleaseStageRun
        {
            Feature = feature,
            StageName = "business-analyst",
            Status = status,
            Phase = phase,
            AcpSessionId = Guid.NewGuid().ToString(),
            CheckpointJson = checkpointSignal is { } signal
                ? new StageCheckpoint(signal, StagePhase.GuidedQA, DateTimeOffset.UtcNow).Serialize()
                : null,
        };
        feature.StageRuns.Add(run);
        release.Features.Add(feature);
        db.Releases.Add(release);
        db.SaveChanges();
        return feature.Id;
    }

    private ReleaseStageRun ReadRun(Guid featureId)
    {
        using var db = OpenDb();
        return db.ReleaseStageRuns.Single(r => r.ReleaseFeatureId == featureId);
    }

    private DevTeamDbContext OpenDb() =>
        new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    /// <summary>
    /// Same shape as ApiIntegrationTests.AppFactory: its own SQLite file and nothing reaching
    /// the network — only the pieces the recovery pass needs, with the much larger container
    /// (fakes, real SemaNami listener) deliberately left out.
    /// </summary>
    private sealed class RecoveryAppFactory(string dbPath, Func<bool>? pidIsAlive = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("DataDirectory", Path.GetDirectoryName(dbPath) ?? ".");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.Single(d =>
                    d.ServiceType == typeof(IDbContextFactory<DevTeamDbContext>));
                services.Remove(descriptor);
                services.AddDbContextFactory<DevTeamDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                if (pidIsAlive is { } alive)
                    services.AddSingleton<Func<int, bool>>(_ => alive());

                // Same seam as in ApiIntegrationTests: the SemaNami listener hits a real Telegram
                // bot when the environment carries a token, so tests swap it out explicitly.
                var listener = services.FirstOrDefault(d =>
                    d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == typeof(SemaNamiListenerService));
                if (listener is not null)
                    services.Remove(listener);
            });
        }
    }
}
