using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class ScratchRetryDbgTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGateRunner _gateRunner = new();
    private readonly FakeGitService _gitService = new();
    private readonly FakeGitCredentialStore _credentialStore = new();
    private readonly FakeBrokerCoordinator _coordinator = new();
    private readonly ActiveTurnTracker _turnTracker = new();

    public ScratchRetryDbgTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateFactory()
        => new ScratchFactory(_connection);

    private sealed class ScratchFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new ChangeTrackerLogger())
                .LogTo(m => System.Console.WriteLine("[SQL] " + m), Microsoft.Extensions.Logging.LogLevel.Information)
                .EnableSensitiveDataLogging()
                .Options);
    }

    private sealed class ChangeTrackerLogger : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            System.Console.WriteLine("[TRACKER] " + eventData.Context!.ChangeTracker.DebugView.ShortView);
            return base.SavingChanges(eventData, result);
        }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            System.Console.WriteLine("[TRACKER] " + eventData.Context!.ChangeTracker.DebugView.ShortView);
            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private static GateResult Pass(string evidence) => new(true, "OK", evidence);
    private static GateResult Fail(string evidence) => new(false, "failed", evidence);

    [Fact]
    public async Task Reproduce_RetryStage_OnBlockedGate()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("map"), Pass("context"), Fail("REQ-004 missing Given")]);
        var engine = new WorkflowEngine(
            CreateFactory(), _gateRunner, _coordinator, _broadcaster,
            _gitService, new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
            new ModelCatalogService(_coordinator), _credentialStore, _turnTracker);

        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        var first = await engine.RunGatesAsync(featureId, CancellationToken.None);
        System.Console.WriteLine("[DBG] status after drive: " + first.StageRuns.Single(sr => sr.StageName == "business-analyst").Status);

        var result = await engine.RetryStageAsync(featureId, targetStageName: null, CancellationToken.None);
        System.Console.WriteLine("[DBG] RetryStage succeeded, run count = " + result.StageRuns.Count(sr => sr.StageName == "business-analyst"));
    }

    [Fact]
    public async Task Debug_FreshRun_TrackedState()
    {
        _gateRunner.Results.AddRange([Pass("scaffold"), Pass("core"), Pass("map"), Pass("context"), Fail("REQ-004 missing Given")]);
        var engine = new WorkflowEngine(
            CreateFactory(), _gateRunner, _coordinator, _broadcaster,
            _gitService, new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
            new ModelCatalogService(_coordinator), _credentialStore, _turnTracker);

        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        await using (var probe = CreateFactory().CreateDbContext())
        {
            foreach (var row in probe.ReleaseStageRuns.Where(r => r.ReleaseFeatureId == featureId).ToList())
                System.Console.WriteLine($"[DBG] DB row: Id={row.Id} Att={row.Attempt} Status={row.Status} StartedAt={row.StartedAt}");
        }

        await using var db = CreateFactory().CreateDbContext();
        var feature = await db.ReleaseFeatures
            .Include(f => f.Release)
            .Include(f => f.StageRuns)
            .Include(f => f.FlowPosition)
            .Include(f => f.Signoffs)
            .SingleAsync(f => f.Id == featureId);

        foreach (var prior in feature.StageRuns.Where(sr => sr.StageName == "business-analyst"))
        {
            prior.Status = ReleaseStageStatus.Stale;
            prior.ReadyToProceed = false;
            prior.FinishedAt = DateTimeOffset.UtcNow;
            prior.Summary = "Superseded by a fresh attempt.";
        }

        var nextAttempt = feature.StageRuns.Count(sr => sr.StageName == "business-analyst") > 0
            ? feature.StageRuns.Where(sr => sr.StageName == "business-analyst").Max(sr => sr.Attempt) + 1
            : 1;

        var freshRun = new ReleaseStageRun
        {
            Feature = feature,
            StageName = "business-analyst",
            Status = ReleaseStageStatus.Active,
            Phase = StagePhase.GuidedQA,
            Attempt = nextAttempt,
            ReadyToProceed = false,
        };
        System.Console.WriteLine($"[DBG] freshRun.Id = {freshRun.Id}");
        foreach (var e in db.ChangeTracker.Entries<ReleaseStageRun>())
            System.Console.WriteLine($"[DBG] tracked {e.Entity.Id} same= {ReferenceEquals(e.Entity, freshRun)} Att={e.Entity.Attempt} Status={e.Entity.Status} StartedAt={e.Entity.StartedAt} Stage={e.Entity.StageName} InList={ReferenceEquals(feature.StageRuns, null)} Surrogate={e.State}");
        db.ReleaseStageRuns.Add(freshRun);
        System.Console.WriteLine("[DBG] entry.State right after DbSet Add: " + db.Entry(freshRun).State);
        System.Console.WriteLine("[DBG] tracked ReleaseStageRun count before save: " + db.ChangeTracker.Entries<ReleaseStageRun>().Count());
        System.Console.WriteLine("[DBG] existing tracked run id(s): "
            + string.Join(", ", db.ChangeTracker.Entries<ReleaseStageRun>().Select(e => e.Entity.Id + "=" + e.State)));

        await db.SaveChangesAsync(CancellationToken.None);
        System.Console.WriteLine("[DBG] AFTER SAVE — tracked runs: "
            + string.Join(", ", db.ChangeTracker.Entries<ReleaseStageRun>().Select(e => e.Entity.Id + "=" + e.State)));
        var rows = db.ReleaseStageRuns.Count(sr => sr.ReleaseFeatureId == featureId);
        System.Console.WriteLine("[DBG] ReleaseStageRuns rows for feature after save: " + rows);
    }
}