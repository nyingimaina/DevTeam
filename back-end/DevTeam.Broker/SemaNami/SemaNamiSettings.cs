using DevTeam.Broker.Domain;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.SemaNami;

/// <summary>
/// Whether the live SemaNami Telegram channel is active, persisted so it survives a restart —
/// same AppSetting-backed load/save pattern as NotificationSettings. Off by default: even when
/// on, TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID (SemaNami's existing env var convention) must also be
/// set for SemaNamiListenerService to actually start.
/// </summary>
public sealed class SemaNamiSettings
{
    public const string EnabledKey = "semanami.enabled";

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public SemaNamiSettings(IDbContextFactory<DevTeamDbContext> dbFactory) => _dbFactory = dbFactory;

    public bool Enabled { get; private set; }

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Name == EnabledKey, ct);
            Enabled = row is not null && bool.TryParse(row.Value, out var parsed) && parsed;
        }
        catch (Exception)
        {
            // A settings read must never stop the app starting.
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Name == EnabledKey, ct);
        if (row is null)
            db.AppSettings.Add(new AppSetting { Name = EnabledKey, Value = enabled.ToString() });
        else
            row.Value = enabled.ToString();

        await db.SaveChangesAsync(ct);
        Enabled = enabled;
    }
}
