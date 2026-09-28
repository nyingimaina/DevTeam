using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Server;

/// <summary>
/// Stamps a periodic "this process owns the data directory" record so a booting broker can tell
/// "the previous owner died mid-prompt" (safe to heal) from "another broker is running right
/// now, possibly a test host that merely resolved Program against the shared database" — the
/// exact confusion whose recovery pass tore down the live app's working turn (observed
/// 2026-09-28 19:15 / 19:18, twice, from the agent's own dotnet-test boots).
/// 
/// The row is never cleared on shutdown on purpose: a stale beat whose pid no longer exists is
/// precisely the evidence the crash recoverer needs for a genuine restart heal.
/// </summary>
public sealed class BrokerHeartbeatService(
    IDbContextFactory<DevTeamDbContext> dbFactory,
    ILogger<BrokerHeartbeatService> logger) : IHostedService, IDisposable
{
    internal const string OwnerKey = "devteam.broker.owner";

    private static readonly TimeSpan BeatInterval = TimeSpan.FromSeconds(15);

    private Timer? _timer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // One beat immediately (a boot that races a heal reads us), then on a timer.
        _timer = new Timer(Beat, null, dueTime: TimeSpan.Zero, period: BeatInterval);
        return Task.CompletedTask;
    }

    private void Beat(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var value = System.Text.Json.JsonSerializer.Serialize(new
                {
                    pid = Environment.ProcessId,
                    beat = DateTimeOffset.UtcNow,
                });
                var row = await db.AppSettings.FirstOrDefaultAsync(a => a.Name == OwnerKey);
                if (row is null)
                {
                    db.AppSettings.Add(new AppSetting { Name = OwnerKey, Value = value });
                }
                else
                {
                    row.Value = value;
                }
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                // A skipped beat must never take the broker down; the recoverer treats an
                // unreadable/stale record as "no live owner" and heals on its own judgement.
                logger.LogDebug(ex, "Broker heartbeat skipped.");
            }
        });
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _timer?.Dispose();
}
