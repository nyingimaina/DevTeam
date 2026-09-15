using DevTeam.Broker.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

public class ReleaseDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public ReleaseDbContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private DevTeamDbContext CreateDbContext() => new(new DbContextOptionsBuilder<DevTeamDbContext>()
        .UseSqlite(_connection)
        .Options);

    [Fact]
    public void EnsureCreated_BuildsAllReleaseTables()
    {
        using var db = CreateDbContext();
        var tables = db.Database.SqlQuery<string>($$"""
            SELECT name FROM sqlite_master
            WHERE type='table' AND name NOT LIKE 'sqlite_%'
            AND name NOT LIKE '__EF%'
            """).ToList();

        Assert.Contains("Releases", tables);
        Assert.Contains("ReleaseFeatures", tables);
        Assert.Contains("ReleaseRequirements", tables);
        Assert.Contains("FeatureSlices", tables);
        Assert.Contains("ReleaseStageRuns", tables);
        Assert.Contains("ReleaseGateChecks", tables);
        Assert.Contains("ReviewFindings", tables);
        Assert.Contains("ReleaseSignoffs", tables);
        Assert.Contains("ReleaseGuidanceNotes", tables);
        Assert.Contains("ReleaseUsageLedgers", tables);
        Assert.Contains("ReleaseFlowPositions", tables);
    }

    [Fact]
    public void Release_WithFeaturesRequirementsAndSlice_RoundTrips()
    {
        Guid releaseId;
        using (var db = CreateDbContext())
        {
            var release = new DevTeamRelease
            {
                WorkspacePath = @"C:\work\proj",
                Title = "Ship 1.0",
                Version = "1.0.0",
            };
            var feature = new ReleaseFeature
            {
                Release = release,
                Key = "feat-001",
                Title = "Login",
                Slice = new FeatureSlice
                {
                    ArtifactDirectory = @"devteam\features\feat-001",
                    CodePathBack = @"back-end\Features\Login",
                    CodePathFront = @"front-end\app\login",
                    SharedFilesJson = """["Program.cs","DevTeamDbContext.cs"]""",
                },
            };
            feature.Requirements.Add(new ReleaseRequirement
            {
                Feature = feature,
                ReqId = "REQ-001",
                Title = "User can log in",
                AcceptanceCriteria = "Given ... When ... Then ...",
            });
            release.Features.Add(feature);

            db.Releases.Add(release);
            db.SaveChanges();
            releaseId = release.Id;
        }

        using var verify = CreateDbContext();
        var loaded = verify.Releases
            .Include(r => r.Features)
            .ThenInclude(f => f.Slice)
            .Include(r => r.Features)
            .ThenInclude(f => f.Requirements)
            .Single(r => r.Id == releaseId);

        Assert.Equal("Ship 1.0", loaded.Title);
        Assert.Single(loaded.Features);
        var loadedFeature = loaded.Features[0];
        Assert.Equal("feat-001", loadedFeature.Key);
        Assert.NotNull(loadedFeature.Slice);
        Assert.Equal(@"back-end\Features\Login", loadedFeature.Slice!.CodePathBack);
        Assert.Single(loadedFeature.Requirements);
        Assert.Equal("REQ-001", loadedFeature.Requirements[0].ReqId);
    }

    [Fact]
    public void StageRun_WithGatesFindingsGuidanceUsageAndSignoff_RoundTrips()
    {
        Guid stageRunId;
        using (var db = CreateDbContext())
        {
            var release = new DevTeamRelease { WorkspacePath = @"C:\work\proj" };
            var feature = new ReleaseFeature { Release = release, Key = "feat-001", Title = "Login" };
            var parentStage = new ReleaseStageRun
            {
                Feature = feature,
                StageName = "business-analyst",
                Status = ReleaseStageStatus.Complete,
                Summary = "Specs written",
            };
            parentStage.GateChecks.Add(new ReleaseGateCheck
            {
                StageRun = parentStage,
                Name = "gherkin_validator",
                Passed = true,
                EvidenceText = "3 criteria validated",
            });
            parentStage.Findings.Add(new ReviewFinding
            {
                StageRun = parentStage,
                Target = "business-analyst",
                Kind = ReviewFindingKind.Requirement,
                Severity = ReviewFindingSeverity.Major,
                Summary = "REQ-002 acceptance criteria is ambiguous",
                Status = ReviewFindingStatus.Resolved,
                ResolutionNote = "Clarified in v2",
            });
            parentStage.GuidanceNotes.Add(new ReleaseGuidanceNote
            {
                StageRun = parentStage,
                Text = "Focus on the login flow first",
                AddedBy = "user",
            });
            db.ReleaseStageRuns.Add(parentStage);

            db.ReleaseSignoffs.Add(new ReleaseSignoff
            {
                Feature = feature,
                StageName = "requirements-approval",
                Approved = true,
                ApprovedBy = "user",
                Comment = "Looks good",
            });
            db.ReleaseFlowPositions.Add(new ReleaseFlowPosition
            {
                Feature = feature,
                CurrentStageIndex = 1,
                CurrentStageName = "developer",
            });
            db.ReleaseUsageLedgers.Add(new ReleaseUsageLedger
            {
                StageRun = parentStage,
                InputTokens = 30,
                OutputTokens = 5,
                TotalTokens = 35,
            });

            db.SaveChanges();
            stageRunId = parentStage.Id;
        }

        using var verify = CreateDbContext();
        var loadedStage = verify.ReleaseStageRuns
            .Include(s => s.GateChecks)
            .Include(s => s.Findings)
            .Include(s => s.GuidanceNotes)
            .Single(s => s.Id == stageRunId);

        Assert.Equal(ReleaseStageStatus.Complete, loadedStage.Status);
        Assert.Single(loadedStage.GateChecks);
        Assert.True(loadedStage.GateChecks[0].Passed);
        Assert.Equal("gherkin_validator", loadedStage.GateChecks[0].Name);
        Assert.Single(loadedStage.Findings);
        Assert.Equal(ReviewFindingStatus.Resolved, loadedStage.Findings[0].Status);
        Assert.Equal("REQ-002 acceptance criteria is ambiguous", loadedStage.Findings[0].Summary);
        Assert.Single(loadedStage.GuidanceNotes);
        Assert.Equal("Focus on the login flow first", loadedStage.GuidanceNotes[0].Text);

        var ledger = verify.ReleaseUsageLedgers.Single(l => l.StageRunId == stageRunId);
        Assert.Equal(35, ledger.TotalTokens);

        var signoff = verify.ReleaseSignoffs.Single(s => s.StageName == "requirements-approval");
        Assert.True(signoff.Approved);
        Assert.Equal("user", signoff.ApprovedBy);

        var position = verify.ReleaseFlowPositions.Single();
        Assert.Equal("developer", position.CurrentStageName);
    }

    [Fact]
    public async Task FeatureKey_IsUniquePerRelease()
    {
        using var db = CreateDbContext();
        var release = new DevTeamRelease { WorkspacePath = @"C:\work\proj" };
        db.Releases.Add(release);
        await db.SaveChangesAsync();

        db.ReleaseFeatures.Add(new ReleaseFeature { ReleaseId = release.Id, Key = "feat-001", Title = "A" });
        db.ReleaseFeatures.Add(new ReleaseFeature { ReleaseId = release.Id, Key = "feat-001", Title = "B" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}