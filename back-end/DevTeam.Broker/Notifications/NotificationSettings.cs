using DevTeam.Broker.Domain;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Notifications;

/// <summary>
/// Which events raise a desktop notification, persisted so a choice survives a restart. All
/// default on: the app runs headless, so this is the only way to tell someone something finished
/// or needs them.
/// </summary>
public sealed class NotificationSettings
{
    public const string CompletedKey = "notifications.stageComplete";
    public const string NeedsAttentionKey = "notifications.needsAttention";
    public const string ApprovalKey = "notifications.approvalNeeded";
    public const string SoundKey = "notifications.sound";

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public NotificationSettings(IDbContextFactory<DevTeamDbContext> dbFactory) => _dbFactory = dbFactory;

    public bool StageComplete { get; private set; } = true;
    public bool NeedsAttention { get; private set; } = true;
    public bool ApprovalNeeded { get; private set; } = true;
    public bool Sound { get; private set; } = true;

    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.AppSettings
                .Where(s => s.Name.StartsWith("notifications."))
                .ToDictionaryAsync(s => s.Name, s => s.Value, ct);

            StageComplete = Read(rows, CompletedKey, StageComplete);
            NeedsAttention = Read(rows, NeedsAttentionKey, NeedsAttention);
            ApprovalNeeded = Read(rows, ApprovalKey, ApprovalNeeded);
            Sound = Read(rows, SoundKey, Sound);
        }
        catch (Exception)
        {
            // A settings read must never stop the app starting.
        }
    }

    public async Task SetAsync(
        bool? stageComplete, bool? needsAttention, bool? approvalNeeded, bool? sound, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        await WriteAsync(db, CompletedKey, stageComplete, ct);
        await WriteAsync(db, NeedsAttentionKey, needsAttention, ct);
        await WriteAsync(db, ApprovalKey, approvalNeeded, ct);
        await WriteAsync(db, SoundKey, sound, ct);

        StageComplete = stageComplete ?? StageComplete;
        NeedsAttention = needsAttention ?? NeedsAttention;
        ApprovalNeeded = approvalNeeded ?? ApprovalNeeded;
        Sound = sound ?? Sound;
    }

    private static async Task WriteAsync(DevTeamDbContext db, string name, bool? value, CancellationToken ct)
    {
        if (value is not { } wanted)
            return;

        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Name == name, ct);
        if (row is null)
            db.AppSettings.Add(new AppSetting { Name = name, Value = wanted.ToString() });
        else
            row.Value = wanted.ToString();

        await db.SaveChangesAsync(ct);
    }

    private static bool Read(IReadOnlyDictionary<string, string> rows, string name, bool fallback)
        => rows.TryGetValue(name, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;
}
