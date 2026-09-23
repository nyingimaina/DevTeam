using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Workflow;
using DevTeam.Tests.Gates.Readiness;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DevTeam.Tests;

/// <summary>
/// The release-level half of the ship gate: it must record a report, pin an attestation to the
/// exact commit it checked, and invalidate that attestation the moment the branch moves.
/// </summary>
public class ShipReadinessGateTests : IDisposable
{
    private const string Workspace = @"C:\work\proj";
    private const string ReleaseBranch = "release/feat-001";
    private readonly SqliteConnection _connection;
    private readonly FakeGitService _gitService = new();

    public ShipReadinessGateTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task CheckRelease_PersistsTheReportAndAShaPinnedAttestation()
    {
        _gitService.HeadCommitSha = "sha-1";
        var releaseId = SeedRelease();
        var gate = CreateGate(new FakeReadinessChecker(passed: true));

        var view = await gate.CheckReleaseAsync(releaseId, CancellationToken.None);

        Assert.True(view.Passed);
        Assert.Equal("release", view.Scope);
        Assert.Equal("1.2.3", view.ReleaseVersion);
        Assert.Single(view.Checks);

        await using var db = CreateFactory().CreateDbContext();
        var attestation = await db.ReadinessAttestations.SingleAsync();
        Assert.True(attestation.Passed);
        Assert.Equal("sha-1", attestation.CommitSha);
        Assert.Equal(ReleaseBranch, attestation.SourceBranch);
        Assert.Single(await db.ReadinessChecks.ToListAsync());
    }

    [Fact]
    public async Task CheckRelease_RecordsAFailingAttestation_SoTheVerdictIsNeverMissing()
    {
        _gitService.HeadCommitSha = "sha-1";
        var releaseId = SeedRelease();
        var gate = CreateGate(new FakeReadinessChecker(passed: false));

        var view = await gate.CheckReleaseAsync(releaseId, CancellationToken.None);

        Assert.False(view.Passed);
        Assert.Contains("didn't all pass", view.BlockerSummary);

        await using var db = CreateFactory().CreateDbContext();
        Assert.False((await db.ReadinessAttestations.SingleAsync()).Passed);
    }

    [Fact]
    public async Task HasValidAttestation_IsTrueOnlyForTheExactCommitThatPassed()
    {
        _gitService.HeadCommitSha = "sha-1";
        var releaseId = SeedRelease();
        var gate = CreateGate(new FakeReadinessChecker(passed: true));
        await gate.CheckReleaseAsync(releaseId, CancellationToken.None);

        Assert.True(await gate.HasValidAttestationAsync(Workspace, ReleaseBranch, CancellationToken.None));

        // A new commit after the check invalidates it — the checks must run again.
        _gitService.HeadCommitSha = "sha-2";
        Assert.False(await gate.HasValidAttestationAsync(Workspace, ReleaseBranch, CancellationToken.None));
    }

    [Fact]
    public async Task HasValidAttestation_IsFalseWithoutAnyAttestation()
    {
        var gate = CreateGate(new FakeReadinessChecker(passed: true));

        Assert.False(await gate.HasValidAttestationAsync(Workspace, ReleaseBranch, CancellationToken.None));
    }

    [Fact]
    public async Task HasValidAttestation_IsFalseWhenTheLatestCheckFailed()
    {
        _gitService.HeadCommitSha = "sha-1";
        var releaseId = SeedRelease();
        var gate = CreateGate(new FakeReadinessChecker(passed: false));
        await gate.CheckReleaseAsync(releaseId, CancellationToken.None);

        Assert.False(await gate.HasValidAttestationAsync(Workspace, ReleaseBranch, CancellationToken.None));
    }

    [Fact]
    public async Task GetLatest_ReturnsTheMostRecentReport()
    {
        _gitService.HeadCommitSha = "sha-1";
        var releaseId = SeedRelease();
        var gate = CreateGate(new FakeReadinessChecker(passed: false));
        await gate.CheckReleaseAsync(releaseId, CancellationToken.None);

        var passing = CreateGate(new FakeReadinessChecker(passed: true));
        var latest = await passing.CheckReleaseAsync(releaseId, CancellationToken.None);

        var fetched = await passing.GetLatestAsync(releaseId, CancellationToken.None);
        Assert.Equal(latest.Id, fetched!.Id);
        Assert.True(fetched.Passed);
    }

    [Fact]
    public void ProtectedBranches_CoversMainAndMaster_ButNotDevelop()
    {
        Assert.True(ProtectedBranches.Contains("main"));
        Assert.True(ProtectedBranches.Contains("master"));
        Assert.True(ProtectedBranches.Contains("MAIN"));
        Assert.False(ProtectedBranches.Contains("develop"));
        Assert.False(ProtectedBranches.Contains("release/feat-001"));
        Assert.False(ProtectedBranches.Contains(null));
    }

    [Fact]
    public async Task GetHistory_ReturnsEveryRunOldestFirst()
    {
        _gitService.HeadCommitSha = "sha-1";
        var releaseId = SeedRelease();
        await CreateGate(new FakeReadinessChecker(passed: false)).CheckReleaseAsync(releaseId, CancellationToken.None);
        var passing = CreateGate(new FakeReadinessChecker(passed: true));
        await passing.CheckReleaseAsync(releaseId, CancellationToken.None);

        var history = await passing.GetHistoryAsync(releaseId, CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.False(history[0].Passed);
        Assert.True(history[1].Passed);
    }

    private ShipReadinessGate CreateGate(IReadinessChecker checker)
        => new(CreateFactory(), checker, _gitService);

    private Guid SeedRelease()
    {
        using var db = CreateFactory().CreateDbContext();
        var release = new DevTeamRelease
        {
            WorkspacePath = Workspace,
            BranchName = ReleaseBranch,
            Version = "1.2.3",
            Title = "Test release",
            Status = ReleaseStatus.Ready,
        };
        db.Releases.Add(release);
        db.SaveChanges();
        return release.Id;
    }

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}
