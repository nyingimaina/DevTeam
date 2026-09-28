using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class StageArtifactsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGateRunner _gateRunner = new();
    private readonly FakeGitService _gitService = new();
    private readonly FakeGitCredentialStore _credentialStore = new();
    private readonly FakeBrokerCoordinator _coordinator = new();

    public StageArtifactsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, _coordinator, _broadcaster,
        _gitService, new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
        new ModelCatalogService(_coordinator), _credentialStore, new ActiveTurnTracker());

    private sealed class TempDir(string path) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public async Task GetStageArtifacts_ReturnsFileContentForExistingArtifacts()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-artifacts-" + Guid.NewGuid().ToString("N")));
        var handoffPath = Path.Combine(workspace.Path, "devteam", "features", "feat-001", "handoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(handoffPath)!);
        File.WriteAllText(handoffPath, "# Handoff\nAll requirements approved.");
        var specsPath = Path.Combine(workspace.Path, "devteam", "features", "feat-001", "specs.feature");
        File.WriteAllText(specsPath, "Feature: Login");

        var engine = CreateEngine();
        var release = await engine.StartReleaseWithFeatureAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);

        var artifacts = await engine.GetStageArtifactsAsync(featureId, stageRun.Id, CancellationToken.None);

        Assert.Equal(2, artifacts.Count);
        var specs = Assert.Single(artifacts, a => a.RelativePath == "devteam/features/feat-001/specs.feature");
        Assert.Equal("Feature: Login", specs.Content);
        var handoff = Assert.Single(artifacts, a => a.RelativePath == "devteam/features/feat-001/handoff.md");
        Assert.Equal("# Handoff\nAll requirements approved.", handoff.Content);
    }

    [Fact]
    public async Task GetStageArtifacts_ReportsDirectoryArtifactsWithNullContent()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-artifacts-" + Guid.NewGuid().ToString("N")));
        var codeDir = Path.Combine(workspace.Path, "devteam", "features", "feat-001", "code");
        Directory.CreateDirectory(codeDir);

        var engine = CreateEngine();
        var release = await engine.StartReleaseWithFeatureAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;

        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        await engine.SignoffAsync(featureId, "business-analyst", "pm", null, CancellationToken.None);

        var developerRun = await engine.RunStageAsync(featureId, CancellationToken.None);
        var developerStageRunId = developerRun.StageRuns.Single(sr => sr.StageName == "developer").Id;

        var artifacts = await engine.GetStageArtifactsAsync(featureId, developerStageRunId, CancellationToken.None);

        var codeArtifact = Assert.Single(artifacts, a => a.RelativePath == "devteam/features/feat-001/code/");
        Assert.Null(codeArtifact.Content);
    }

    [Fact]
    public async Task GetStageArtifacts_TruncatesOversizedFiles()
    {
        using var workspace = new TempDir(Path.Combine(Path.GetTempPath(), "devteam-artifacts-" + Guid.NewGuid().ToString("N")));
        var handoffPath = Path.Combine(workspace.Path, "devteam", "features", "feat-001", "handoff.md");
        Directory.CreateDirectory(Path.GetDirectoryName(handoffPath)!);
        File.WriteAllText(handoffPath, new string('x', 25 * 1024));
        var specsPath = Path.Combine(workspace.Path, "devteam", "features", "feat-001", "specs.feature");
        File.WriteAllText(specsPath, "Feature: Login");

        var engine = CreateEngine();
        var release = await engine.StartReleaseWithFeatureAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);

        var artifacts = await engine.GetStageArtifactsAsync(featureId, stageRun.Id, CancellationToken.None);

        var handoff = Assert.Single(artifacts, a => a.RelativePath == "devteam/features/feat-001/handoff.md");
        Assert.NotNull(handoff.Content);
        Assert.EndsWith("…(truncated)", handoff.Content);
        Assert.True(handoff.Content!.Length < 25 * 1024);
    }

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}
