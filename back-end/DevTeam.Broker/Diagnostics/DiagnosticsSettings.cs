using DevTeam.Broker.Domain;
using Microsoft.EntityFrameworkCore;
using Serilog.Core;
using Serilog.Events;

namespace DevTeam.Broker.Diagnostics;

/// <summary>
/// Operator-facing diagnostics preferences, persisted so they survive a restart (a support
/// investigation often spans several runs). Today there is one: a "verbose logging" switch that
/// raises the Serilog minimum level so a specialist can see the full detail of what happened,
/// and drops it back afterwards.
/// </summary>
public sealed class DiagnosticsSettings
{
    public const string VerboseLoggingKey = "diagnostics.verboseLogging";

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly LoggingLevelSwitch _levelSwitch;

    public DiagnosticsSettings(IDbContextFactory<DevTeamDbContext> dbFactory, LoggingLevelSwitch levelSwitch)
    {
        _dbFactory = dbFactory;
        _levelSwitch = levelSwitch;
    }

    /// <summary>True while detailed logging is being captured.</summary>
    public bool VerboseLogging { get; private set; }

    /// <summary>Loads the persisted preference. Never throws — a settings read must not stop the app.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Name == VerboseLoggingKey, ct);
            Apply(row is not null && bool.TryParse(row.Value, out var enabled) && enabled);
        }
        catch (Exception)
        {
            Apply(false);
        }
    }

    public async Task SetVerboseLoggingAsync(bool enabled, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Name == VerboseLoggingKey, ct);
        if (row is null)
            db.AppSettings.Add(new AppSetting { Name = VerboseLoggingKey, Value = enabled.ToString() });
        else
            row.Value = enabled.ToString();

        await db.SaveChangesAsync(ct);
        Apply(enabled);
    }

    private void Apply(bool enabled)
    {
        VerboseLogging = enabled;
        // Verbose (not Debug) on purpose: it includes EF's SQL and framework diagnostics, which
        // is exactly what a specialist needs to reconstruct a failure after the fact.
        _levelSwitch.MinimumLevel = enabled ? LogEventLevel.Verbose : LogEventLevel.Information;
    }
}
