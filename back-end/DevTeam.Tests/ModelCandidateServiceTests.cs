using DevTeam.Broker.Domain;
using DevTeam.Broker.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DevTeam.Tests;

/// <summary>
/// These use the exact wording opencode writes to its own log — the only place a provider
/// refusal appears, and the reason the app used to sit in limbo instead of explaining itself.
/// </summary>
public class ProviderFailureClassifierTests
{
    [Fact]
    public void ClassifiesTheRateLimitTheAppActuallyHit()
    {
        var failure = ProviderFailureClassifier.Classify(
            """timestamp=2026-09-21T18:48:49Z level=ERROR message="stream error" providerID=opencode modelID=big-pickle session.id=ses_x small=false agent=build mode=primary error.error="AI_APICallError: Error from provider (Console): Rate limit exceeded. Please try again later.""");

        Assert.Equal(ProviderFailureKind.RateLimited, failure.Kind);
        Assert.Contains("rate-limiting", failure.PlainReason);
        Assert.Equal(ProviderFailureClassifier.RateLimitCooldown, failure.Cooldown);
    }

    [Theory]
    [InlineData("AI_APICallError: Error from provider (Console): Upstream request failed: Endpoint is unavailable.", ProviderFailureKind.Unavailable)]
    [InlineData("AI_APICallError: Cannot connect to API: Unable to connect. Is the computer able to access the url?", ProviderFailureKind.ConnectFailed)]
    [InlineData("AI_APICallError: model 'qwen3:8b' not found", ProviderFailureKind.ModelInvalid)]
    [InlineData("AI_APICallError: This model is currently experiencing high demand.", ProviderFailureKind.RateLimited)]
    public void ClassifiesTheOtherFailureWording(string line, ProviderFailureKind expected)
        => Assert.Equal(expected, ProviderFailureClassifier.Classify(line).Kind);

    [Fact]
    public void ACleanLineIsNotAFailure()
    {
        Assert.False(ProviderFailureClassifier.Classify("level=INFO message=loop session.id=ses_x step=2").IsFailure);
        Assert.False(ProviderFailureClassifier.Classify("").IsFailure);
        Assert.False(ProviderFailureClassifier.Classify(null).IsFailure);
    }
}

public class ModelCandidateServiceTests : IDisposable
{
    private const string Workspace = @"D:\apps\calculator";
    private readonly SqliteConnection _connection;

    public ModelCandidateServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SeedsTheCuratedListOnce_WithTheDefaultFirst()
    {
        var service = new ModelCandidateService(CreateFactory());

        var seeded = await service.ListSeededAsync(Workspace, CancellationToken.None);

        Assert.Equal(ModelCatalog.SeedLimit, seeded.Count);
        Assert.Equal(ModelCatalog.PinnedFirst, seeded[0].ModelId);
        Assert.All(seeded, c => Assert.True(c.Enabled));
        Assert.All(seeded, c => Assert.False(c.UserAdded));

        // Seeding is idempotent — a second call must not duplicate the list.
        var again = await service.ListSeededAsync(Workspace, CancellationToken.None);
        Assert.Equal(seeded.Count, again.Count);
    }

    [Fact]
    public async Task TheSeedSpreadsProviders_SoAFailureHasSomewhereToGo()
    {
        // A provider-level rate limit hits every model from that provider, so consecutive entries
        // must not all come from the same one.
        var seeded = await new ModelCandidateService(CreateFactory()).ListSeededAsync(Workspace, CancellationToken.None);

        var providers = seeded.Select(c => c.ModelId.Split('/')[0]).ToList();
        for (var i = 1; i < providers.Count; i++)
            Assert.NotEqual(providers[i - 1], providers[i]);
    }

    [Fact]
    public async Task AddedModelsAreMarkedUserAdded_AndLandAtTheEnd()
    {
        var service = new ModelCandidateService(CreateFactory());
        var seeded = await service.ListSeededAsync(Workspace, CancellationToken.None);

        var added = await service.AddAsync(Workspace, "some/other-model", CancellationToken.None);

        Assert.True(added.UserAdded);
        Assert.Equal(seeded.Count, added.Priority);
    }

    [Fact]
    public async Task AddingTheSameModelTwice_IsRejected()
    {
        var service = new ModelCandidateService(CreateFactory());
        await service.ListSeededAsync(Workspace, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddAsync(Workspace, ModelCatalog.PinnedFirst, CancellationToken.None));
    }

    [Fact]
    public async Task AFailedCandidateIsSkippedUntilItsCooldownExpires()
    {
        var service = new ModelCandidateService(CreateFactory());
        var seeded = await service.ListSeededAsync(Workspace, CancellationToken.None);
        var first = seeded[0];

        await service.RecordFailureAsync(first.Id, new ProviderFailure(
            ProviderFailureKind.RateLimited, "busy", TimeSpan.FromMinutes(3)), CancellationToken.None);

        var active = await service.ResolveActiveAsync(Workspace, CancellationToken.None);
        Assert.NotNull(active);
        Assert.NotEqual(first.Id, active!.Id);
        Assert.Equal(seeded[1].ModelId, active.ModelId);

        // The cooldown is explained, not just applied.
        var cooled = (await service.ListAsync(Workspace, CancellationToken.None)).Single(c => c.Id == first.Id);
        Assert.NotNull(cooled.CooldownUntil);
        Assert.Equal("RateLimited", cooled.LastFailureKind);
    }

    [Fact]
    public async Task ADisabledCandidateIsSkipped()
    {
        var service = new ModelCandidateService(CreateFactory());
        var seeded = await service.ListSeededAsync(Workspace, CancellationToken.None);

        await service.SetEnabledAsync(seeded[0].Id, false, CancellationToken.None);

        var active = await service.ResolveActiveAsync(Workspace, CancellationToken.None);
        Assert.Equal(seeded[1].Id, active!.Id);
    }

    [Fact]
    public async Task ReorderingRewritesPriority()
    {
        var service = new ModelCandidateService(CreateFactory());
        var seeded = await service.ListSeededAsync(Workspace, CancellationToken.None);

        var reversed = seeded.Select(c => c.Id).Reverse().ToList();
        await service.ReorderAsync(Workspace, reversed, CancellationToken.None);

        var after = await service.ListAsync(Workspace, CancellationToken.None);
        Assert.Equal(reversed, after.Select(c => c.Id));
    }

    [Fact]
    public async Task RecordedCooldownsArePerWorkspace()
    {
        var service = new ModelCandidateService(CreateFactory());
        var seeded = await service.ListSeededAsync(Workspace, CancellationToken.None);
        await service.RecordFailureAsync(seeded[0].Id, new ProviderFailure(
            ProviderFailureKind.Unavailable, "down", TimeSpan.FromSeconds(30)), CancellationToken.None);

        var other = await service.ListSeededAsync(@"D:\apps\other", CancellationToken.None);

        Assert.All(other, c => Assert.Null(c.CooldownUntil));
    }

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}
