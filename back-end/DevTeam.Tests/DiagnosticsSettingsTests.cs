using DevTeam.Broker.Diagnostics;
using DevTeam.Broker.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace DevTeam.Tests;

/// <summary>Captures log entries so tests can assert on what a specialist would actually see.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception), exception));

    public bool Has(LogLevel level) => Entries.Any(e => e.Level == level);
}

public class DiagnosticsSettingsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Information);

    public DiagnosticsSettingsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Default_IsNormalLogging()
    {
        var settings = Create();

        Assert.False(settings.VerboseLogging);
        Assert.Equal(LogEventLevel.Information, _levelSwitch.MinimumLevel);
    }

    [Fact]
    public async Task TurningItOn_RaisesTheLevelAndPersists()
    {
        var settings = Create();

        await settings.SetVerboseLoggingAsync(true, CancellationToken.None);

        Assert.True(settings.VerboseLogging);
        Assert.Equal(LogEventLevel.Verbose, _levelSwitch.MinimumLevel);

        // A restart must keep collecting — an investigation usually spans several runs.
        var restarted = Create();
        await restarted.LoadAsync(CancellationToken.None);
        Assert.True(restarted.VerboseLogging);
        Assert.Equal(LogEventLevel.Verbose, _levelSwitch.MinimumLevel);
    }

    [Fact]
    public async Task TurningItOff_IsAlsoRemembered()
    {
        var settings = Create();
        await settings.SetVerboseLoggingAsync(true, CancellationToken.None);

        await settings.SetVerboseLoggingAsync(false, CancellationToken.None);

        Assert.False(settings.VerboseLogging);
        Assert.Equal(LogEventLevel.Information, _levelSwitch.MinimumLevel);
    }

    private DiagnosticsSettings Create() => new(CreateFactory(), _levelSwitch);

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new TestDbContextFactory(_connection);

    private sealed class TestDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}
