using DevTeam.Broker.Domain;
using DevTeam.Broker.SemaNami;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests.SemaNami;

public class SemaNamiSettingsTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public SemaNamiSettingsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private SemaNamiSettings Create() => new(CreateFactory());

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    [Fact]
    public void Default_IsDisabled()
    {
        var settings = Create();

        Assert.False(settings.Enabled);
    }

    [Fact]
    public async Task EnablingIt_PersistsAcrossARestart()
    {
        var settings = Create();

        await settings.SetEnabledAsync(true, CancellationToken.None);
        Assert.True(settings.Enabled);

        var restarted = Create();
        await restarted.LoadAsync(CancellationToken.None);
        Assert.True(restarted.Enabled);
    }

    [Fact]
    public async Task DisablingIt_IsAlsoRemembered()
    {
        var settings = Create();
        await settings.SetEnabledAsync(true, CancellationToken.None);

        await settings.SetEnabledAsync(false, CancellationToken.None);

        Assert.False(settings.Enabled);
        var restarted = Create();
        await restarted.LoadAsync(CancellationToken.None);
        Assert.False(restarted.Enabled);
    }

    [Fact]
    public async Task LoadAsync_NoDatabaseYet_DoesNotThrow()
    {
        // A settings read must never stop the app starting — mirrors NotificationSettings'
        // own defensive catch-all.
        await using var db = await CreateFactory().CreateDbContextAsync();
        await db.Database.EnsureDeletedAsync();

        var settings = Create();
        var exception = await Record.ExceptionAsync(() => settings.LoadAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.False(settings.Enabled);
    }
}
