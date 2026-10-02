using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

public class AttentionServiceTests : IDisposable
{
    private const string Workspace = @"C:\work\proj";

    private readonly SqliteConnection _connection;

    public AttentionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = Factory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> Factory() => new Factory2(_connection);

    private async Task<ReleaseFeature> SeedFeatureAsync(
        string stage, ReleaseStageStatus status, bool autoRetrySuppressed = false, string? summary = null,
        string workspace = Workspace, ReleaseStatus releaseStatus = ReleaseStatus.InProgress, int attempt = 1)
    {
        await using var db = Factory().CreateDbContext();
        var release = new DevTeamRelease { WorkspacePath = workspace, Title = "r", Status = releaseStatus, BranchName = "release/r" };
        var feature = new ReleaseFeature
        {
            Release = release,
            Key = "login",
            Title = "Login",
            Status = ReleaseFeatureStatus.InProgress,
            FlowPosition = new ReleaseFlowPosition { CurrentStageName = stage },
        };
        feature.StageRuns.Add(new ReleaseStageRun
        {
            StageName = stage,
            Status = status,
            AutoRetrySuppressed = autoRetrySuppressed,
            Summary = summary,
            Attempt = attempt,
        });
        db.Releases.Add(release);
        db.ReleaseFeatures.Add(feature);
        await db.SaveChangesAsync();
        return feature;
    }

    private Task<IReadOnlyList<AttentionItem>> ListAsync(string? workspace = Workspace)
        => new AttentionService(Factory()).ListAsync(workspace, CancellationToken.None);

    [Fact]
    public async Task AStageWaitingForApproval_AsksForTheUsersApproval_InPlainWords()
    {
        await SeedFeatureAsync("business-analyst", ReleaseStageStatus.BlockedSignoff);

        var item = Assert.Single(await ListAsync());

        Assert.Equal(AttentionKind.Approval, item.Kind);
        Assert.Contains("approv", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Business Analyst", item.Message);
        Assert.Equal("login", item.FeatureKey);
    }

    [Fact]
    public async Task AStageStoppedForADecision_CarriesTheStagesOwnPlainSentence()
    {
        await SeedFeatureAsync("test-runner", ReleaseStageStatus.BlockedGate, autoRetrySuppressed: true,
            summary: "This needs an operator decision before it can continue.");

        var item = Assert.Single(await ListAsync());

        Assert.Equal(AttentionKind.Decision, item.Kind);
        Assert.Contains("decision", item.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("needs an operator decision", item.Message);
    }

    [Fact]
    public async Task ATestTheCheckerThinksIsWrong_IsPutToThePersonAsAQuestionWithBothAnswers()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-attention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DevTeam.Broker.Gates.ArtifactPaths.FeatureDir(workspace, "login"));
        File.WriteAllText(DevTeam.Broker.Gates.ArtifactPaths.TestReportPath(workspace, "login"), """
            ## TEST-1: Wizard > saves
            - Test: front-end/app/Wizard.test.tsx
            - Requirement: REQ-4
            - Verdict: challenge
            - Expected: settings persist
            - Observed: asserts a toast nobody asked for
            - Ruling: pending operator ruling
            """);
        await SeedFeatureAsync("test-runner", ReleaseStageStatus.BlockedGate, autoRetrySuppressed: true,
            summary: "This needs an operator decision before it can continue.", workspace: workspace);

        var item = Assert.Single(await ListAsync(workspace));

        Assert.Contains("a test is wrong", item.Message);
        var ruling = Assert.Single(item.Rulings!);
        Assert.Equal("Wizard > saves", ruling.Test);
        Assert.Equal("REQ-4", ruling.Requirement);
    }

    [Fact]
    public async Task APersonsRuling_IsRecordedInTheFeaturesOwnWorkspace_AndStopsTheQuestion()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-attention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DevTeam.Broker.Gates.ArtifactPaths.FeatureDir(workspace, "login"));
        File.WriteAllText(DevTeam.Broker.Gates.ArtifactPaths.TestReportPath(workspace, "login"),
            "## TEST-1: Wizard > saves\n- Verdict: challenge\n- Ruling: pending operator ruling\n");
        var feature = await SeedFeatureAsync("test-runner", ReleaseStageStatus.BlockedGate, autoRetrySuppressed: true, workspace: workspace);
        var service = new AttentionService(Factory());

        var result = await service.RecordRulingAsync(feature.Id, "Wizard > saves", DevTeam.Broker.Gates.RulingDecision.Reject, CancellationToken.None);

        Assert.True(result.Ok, result.Problem);
        Assert.Empty(DevTeam.Broker.Gates.TestRulings.Pending(workspace, "login"));
    }

    [Fact]
    public async Task ARulingForAFeatureThatIsGone_IsRefusedPolitely()
    {
        var result = await new AttentionService(Factory())
            .RecordRulingAsync(Guid.NewGuid(), "x", DevTeam.Broker.Gates.RulingDecision.Accept, CancellationToken.None);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task AFailedStageThatWillRetryByItself_IsNotAskingForAnything()
    {
        await SeedFeatureAsync("developer", ReleaseStageStatus.BlockedGate, autoRetrySuppressed: false);

        Assert.Empty(await ListAsync());
    }

    [Theory]
    [InlineData(ReleaseStageStatus.Active)]
    [InlineData(ReleaseStageStatus.Complete)]
    [InlineData(ReleaseStageStatus.GatesRunning)]
    public async Task AStageThatIsWorkingOrDone_IsNotAskingForAnything(ReleaseStageStatus status)
    {
        await SeedFeatureAsync("developer", status);

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task OnlyTheLatestAttemptOfTheCurrentStageCounts()
    {
        var feature = await SeedFeatureAsync("test-runner", ReleaseStageStatus.BlockedGate, autoRetrySuppressed: true, attempt: 1);
        await using (var db = Factory().CreateDbContext())
        {
            // A later attempt is running again: the earlier stuck one is history.
            db.ReleaseStageRuns.Add(new ReleaseStageRun
            {
                ReleaseFeatureId = feature.Id,
                StageName = "test-runner",
                Status = ReleaseStageStatus.Active,
                Attempt = 2,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(1),
            });
            await db.SaveChangesAsync();
        }

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task AStuckRunOfAStageTheFeatureHasMovedPast_IsNotAskingForAnything()
    {
        await SeedFeatureAsync("developer", ReleaseStageStatus.BlockedSignoff);
        await using var db = Factory().CreateDbContext();
        var position = await db.ReleaseFlowPositions.SingleAsync();
        position.CurrentStageName = "qa";
        await db.SaveChangesAsync();

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task FinishedAndCancelledReleases_AreNotAskingForAnything()
    {
        await SeedFeatureAsync("qa", ReleaseStageStatus.BlockedSignoff, releaseStatus: ReleaseStatus.Released);
        await SeedFeatureAsync("qa", ReleaseStageStatus.BlockedSignoff, releaseStatus: ReleaseStatus.Cancelled);

        Assert.Empty(await ListAsync());
    }

    [Fact]
    public async Task TheListIsScopedToTheOpenProject()
    {
        await SeedFeatureAsync("qa", ReleaseStageStatus.BlockedSignoff, workspace: @"C:\work\other");

        Assert.Empty(await ListAsync(Workspace));
        Assert.Single(await ListAsync(@"C:\work\other"));
        Assert.Single(await ListAsync(workspace: null));
    }
}

file sealed class Factory2(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
{
    public DevTeamDbContext CreateDbContext()
        => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
}
