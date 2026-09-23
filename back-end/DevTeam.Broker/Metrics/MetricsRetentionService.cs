using DevTeam.Broker.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DevTeam.Broker.Metrics;

/// <summary>
/// Keeps raw turn metrics bounded: after <see cref="RetentionDays"/> the rows are folded into the
/// permanent daily rollup and deleted. The long-term picture survives; the per-turn detail doesn't
/// grow without limit.
/// </summary>
public sealed class MetricsRetentionService : BackgroundService
{
    public const int RetentionDays = 30;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly ILogger<MetricsRetentionService>? _logger;

    public MetricsRetentionService(
        IDbContextFactory<DevTeamDbContext> dbFactory,
        ILogger<MetricsRetentionService>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RollupAndPruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Metrics retention pass failed; will retry.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RollupAndPruneAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-RetentionDays);

        // SQLite can't compare DateTimeOffset, so the cutoff is applied client-side.
        var old = (await db.TurnMetrics.ToListAsync(ct)).Where(m => m.StartedAt < cutoff).ToList();
        if (old.Count == 0)
            return;

        var groups = old.GroupBy(m => new
        {
            Day = m.StartedAt.UtcDateTime.ToString("yyyy-MM-dd"),
            Workspace = m.WorkspacePath ?? string.Empty,
            m.StageName,
            m.Kind,
            Model = m.ModelId ?? string.Empty,
        });

        foreach (var group in groups)
        {
            var key = group.Key;
            var daily = await db.TurnMetricDailies.FirstOrDefaultAsync(
                d => d.Day == key.Day && d.WorkspacePath == key.Workspace && d.StageName == key.StageName
                     && d.Kind == key.Kind && d.ModelId == key.Model, ct);
            if (daily is null)
            {
                daily = new TurnMetricDaily
                {
                    Day = key.Day,
                    WorkspacePath = key.Workspace,
                    StageName = key.StageName,
                    Kind = key.Kind,
                    ModelId = key.Model,
                };
                db.TurnMetricDailies.Add(daily);
            }

            daily.Turns += group.Count();
            daily.InputTokens += group.Sum(m => m.InputTokens);
            daily.OutputTokens += group.Sum(m => m.OutputTokens);
            daily.TotalTokens += group.Sum(m => m.TotalTokens);
            daily.DurationMs += group.Sum(m => m.DurationMs);
            if (group.Any(m => m.CostAmount is not null))
                daily.CostAmount = (daily.CostAmount ?? 0) + group.Sum(m => m.CostAmount ?? 0);
        }

        db.TurnMetrics.RemoveRange(old);
        await db.SaveChangesAsync(ct);
        _logger?.LogInformation("Rolled up and pruned {Count} turn metrics older than {Days} days.", old.Count, RetentionDays);
    }
}
