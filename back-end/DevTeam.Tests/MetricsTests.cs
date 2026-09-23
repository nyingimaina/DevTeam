using DevTeam.Broker.Domain;
using DevTeam.Broker.Metrics;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

public class MetricsDiagnosticsTests
{
    private static MetricsSummary Summary(
        IReadOnlyList<StageMetrics>? perStage = null,
        IReadOnlyList<PromptSectionMetrics>? sections = null,
        long totalTokens = 0,
        long cachedReadTokens = 0)
        => new(
            1, DateTimeOffset.UtcNow, new MetricsScope(null, null, null, 30),
            new MetricsTotals(0, 0, 0, totalTokens, cachedReadTokens, 0, null),
            perStage ?? [], [], [],
            sections ?? [], [], []);

    [Fact]
    public void RetryHeavyStage_IsFlaggedHigh()
    {
        var summary = Summary(perStage: [new StageMetrics("developer", 12, 3, 200_000, 120_000, 0, 60_000, 0)]);

        var finding = MetricsDiagnostics.Findings(summary).Single(f => f.Id == "retry-token-share");

        Assert.Equal("high", finding.Severity);
        Assert.Contains("developer", finding.Title);
    }

    [Fact]
    public void ReviewHeavyStage_IsFlagged()
    {
        var summary = Summary(perStage: [new StageMetrics("qa", 4, 1, 100_000, 0, 60_000, 60_000, 0)]);

        Assert.Contains(MetricsDiagnostics.Findings(summary), f => f.Id == "challenge-token-share");
    }

    [Fact]
    public void GateThrash_IsFlagged()
    {
        var summary = Summary(perStage: [new StageMetrics("developer", 9, 4, 200_000, 0, 0, 60_000, 3)]);

        Assert.Contains(MetricsDiagnostics.Findings(summary), f => f.Id == "gate-thrash");
    }

    [Fact]
    public void ArtifactHeavyPrompt_IsFlagged()
    {
        var summary = Summary(sections:
        [
            new PromptSectionMetrics("artifact", 9000, 900, 0.9),
            new PromptSectionMetrics("base", 1000, 100, 0.1),
        ]);

        var finding = MetricsDiagnostics.Findings(summary).Single(f => f.Id == "prompt-artifact-heavy");

        Assert.Equal("high", finding.Severity);
    }

    [Fact]
    public void LargeSpendWithNoCache_IsFlagged()
    {
        var summary = Summary(totalTokens: 200_000, cachedReadTokens: 0);

        Assert.Contains(MetricsDiagnostics.Findings(summary), f => f.Id == "no-cache");
    }

    [Fact]
    public void CheapHealthyRun_HasNoFindings()
    {
        var summary = Summary(perStage: [new StageMetrics("developer", 2, 1, 8_000, 0, 0, 30_000, 0)]);

        Assert.Empty(MetricsDiagnostics.Findings(summary));
    }
}

public class MetricsMarkdownTests
{
    [Fact]
    public void Render_IncludesTotalsFindingsAndTables()
    {
        var summary = new MetricsSummary(
            1, DateTimeOffset.UtcNow, new MetricsScope("D:\\apps\\CalcV4", null, null, 30),
            new MetricsTotals(5, 10, 5, 15, 0, 60_000, null),
            [new StageMetrics("developer", 5, 2, 15, 0, 0, 60_000, 1)], [], [], [], 
            [new MetricsFinding("retry-token-share", "high", "Retries dominate", new Dictionary<string, object?> { ["stage"] = "developer" }, "Fix the check.")],
            ["All times are UTC."]);

        var markdown = MetricsMarkdown.Render(summary);

        Assert.Contains("# DevTeam efficiency report", markdown);
        Assert.Contains("Retries dominate", markdown);
        Assert.Contains("## Tokens by stage", markdown);
        Assert.Contains("developer", markdown);
        Assert.Contains("All times are UTC.", markdown);
    }
}

public class MetricsServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MetricsServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private DevTeamDbContext CreateDb()
        => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options);

    private IDbContextFactory<DevTeamDbContext> Factory() => new TestFactory(_connection);

    private sealed class TestFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    private void Seed()
    {
        using var db = CreateDb();
        db.TurnMetrics.AddRange(
            new TurnMetric { StageName = "developer", Kind = TurnKind.Stage, ModelId = "m1", TotalTokens = 100, InputTokens = 80, OutputTokens = 20, PromptChars = 400, PromptBreakdownJson = new PromptComposition(100, 0, 0, 0, 0, 0, 0, 300, 0).ToJson(), StartedAt = DateTimeOffset.UtcNow },
            new TurnMetric { StageName = "developer", Kind = TurnKind.Retry, ModelId = "m1", TotalTokens = 100, PromptBreakdownJson = new PromptComposition(100, 0, 0, 0, 0, 0, 0, 300, 0).ToJson(), StartedAt = DateTimeOffset.UtcNow },
            new TurnMetric { StageName = "qa", Kind = TurnKind.Challenge, ModelId = "m2", TotalTokens = 50, StartedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
    }

    [Fact]
    public async Task Summarize_AggregatesTotalsStagesKindsAndPromptSections()
    {
        Seed();
        var service = new MetricsService(Factory());

        var summary = await service.SummarizeAsync(new MetricsScope(null, null, null, 30), CancellationToken.None);

        Assert.Equal(3, summary.Totals.Turns);
        Assert.Equal(250, summary.Totals.TotalTokens);
        var developer = summary.PerStage.Single(s => s.StageName == "developer");
        Assert.Equal(200, developer.TotalTokens);
        Assert.Equal(100, developer.RetryTokens);
        Assert.Contains(summary.PerKind, k => k.Kind == "Retry" && k.TotalTokens == 100);
        var artifact = summary.PromptSections.Single(s => s.Section == "artifact");
        Assert.Equal(600, artifact.TotalChars);
    }

    [Fact]
    public async Task Turns_ReturnsMostRecentFirst()
    {
        Seed();
        var service = new MetricsService(Factory());

        var rows = await service.TurnsAsync(new MetricsScope(null, null, null, 30), 10, CancellationToken.None);

        Assert.Equal(3, rows.Count);
    }
}

public class MetricsRetentionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public MetricsRetentionServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class TestFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    [Fact]
    public async Task RollupAndPrune_FoldsOldRowsIntoDailyAndDeletesThem()
    {
        using (var seed = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options))
        {
            seed.TurnMetrics.Add(new TurnMetric
            {
                StageName = "developer", Kind = TurnKind.Stage, ModelId = "m1", WorkspacePath = "D:\\apps\\X",
                TotalTokens = 42, StartedAt = DateTimeOffset.UtcNow.AddDays(-40),
            });
            seed.SaveChanges();
        }

        await new MetricsRetentionService(new TestFactory(_connection)).RollupAndPruneAsync(CancellationToken.None);

        using var verify = new DevTeamDbContext(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options);
        Assert.Empty(verify.TurnMetrics);
        var daily = Assert.Single(verify.TurnMetricDailies);
        Assert.Equal(1, daily.Turns);
        Assert.Equal(42, daily.TotalTokens);
    }
}
