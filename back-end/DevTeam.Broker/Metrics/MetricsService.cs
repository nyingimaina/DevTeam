using System.Text.Json;

using DevTeam.Broker.Domain;
using DevTeam.Broker.Workflow;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Metrics;

/// <summary>Reads the turn metrics and rolls them up. All aggregation is client-side: the row
/// count within a retention window is small, and SQLite would rather not translate half of this.</summary>
public sealed class MetricsService
{
    private static readonly IReadOnlyList<string> Notes =
    [
        "All times are UTC; durations are milliseconds.",
        "Token counts come from the model provider's usage report; the live mid-turn ContextTokens may disagree.",
        "Cost is only present when the provider reported it; null otherwise.",
        "Prompt chars are measured, not estimated (1 token ≈ 4 chars).",
        "finding.id values are stable — compare them across runs.",
    ];

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public MetricsService(IDbContextFactory<DevTeamDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<MetricsSummary> SummarizeAsync(MetricsScope scope, CancellationToken ct)
    {
        var normalized = scope with { Days = Math.Max(1, scope.Days) };
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-normalized.Days);

        // SQLite can't compare DateTimeOffset, so the date window is applied client-side; the
        // translatable filters (workspace/feature/release) narrow it server-side first.
        var metrics = (await Scoped(db.TurnMetrics, normalized).ToListAsync(ct))
            .Where(m => m.StartedAt >= since)
            .ToList();

        var rawChecks = await ScopedChecks(db.ReleaseGateChecks, normalized)
            .Select(check => new { StageName = check.StageRun.StageName, check.StartedAt })
            .ToListAsync(ct);
        var failedChecks = rawChecks.Where(check => check.StartedAt >= since).ToList();

        var totals = new MetricsTotals(
            metrics.Count,
            metrics.Sum(m => m.InputTokens),
            metrics.Sum(m => m.OutputTokens),
            metrics.Sum(m => m.TotalTokens),
            metrics.Sum(m => m.CachedReadTokens ?? 0),
            metrics.Sum(m => m.DurationMs),
            metrics.Any(m => m.CostAmount is not null) ? metrics.Sum(m => m.CostAmount ?? 0) : null);

        var perStage = metrics
            .GroupBy(m => m.StageName)
            .Select(group => new StageMetrics(
                group.Key,
                group.Count(),
                group.Max(m => m.Attempt),
                group.Sum(m => m.TotalTokens),
                group.Where(m => m.Kind is TurnKind.Retry or TurnKind.Correction).Sum(m => m.TotalTokens),
                group.Where(m => m.Kind is TurnKind.Challenge or TurnKind.GatePrompt).Sum(m => m.TotalTokens),
                group.Sum(m => m.DurationMs),
                failedChecks.Count(check => string.Equals(check.StageName, group.Key, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(stage => stage.TotalTokens)
            .ToList();

        var perKind = metrics
            .GroupBy(m => m.Kind)
            .Select(group => new KindMetrics(group.Key.ToString(), group.Count(), group.Sum(m => m.TotalTokens), group.Sum(m => m.DurationMs)))
            .OrderByDescending(kind => kind.TotalTokens)
            .ToList();

        var perModel = metrics
            .GroupBy(m => string.IsNullOrWhiteSpace(m.ModelId) ? "(unknown)" : m.ModelId!)
            .Select(group => new ModelMetrics(group.Key, group.Count(), group.Sum(m => m.TotalTokens), group.Sum(m => m.CachedReadTokens ?? 0), group.Sum(m => m.DurationMs)))
            .OrderByDescending(model => model.TotalTokens)
            .ToList();

        var summary = new MetricsSummary(
            1, DateTimeOffset.UtcNow, normalized, totals, perStage, perKind, perModel,
            BuildPromptSections(metrics), [], Notes);

        return summary with { Findings = MetricsDiagnostics.Findings(summary) };
    }

    public async Task<IReadOnlyList<TurnMetricRow>> TurnsAsync(MetricsScope scope, int limit, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Max(1, scope.Days));

        var rows = (await Scoped(db.TurnMetrics, scope).ToListAsync(ct))
            .Where(m => m.StartedAt >= since)
            .OrderByDescending(m => m.StartedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToList();

        return rows.Select(m => new TurnMetricRow(
            m.StartedAt, m.StageName, m.FeatureKey, m.ModelId, m.Kind.ToString(), m.Attempt, m.DurationMs,
            m.TimeToFirstEventMs, m.InputTokens, m.OutputTokens, m.TotalTokens, m.CachedReadTokens,
            m.PromptChars, m.TextEvents, m.ThoughtEvents, m.ToolEvents, m.StopReason, m.Outcome.ToString())).ToList();
    }

    private static IQueryable<TurnMetric> Scoped(IQueryable<TurnMetric> query, MetricsScope scope)
    {
        if (!string.IsNullOrWhiteSpace(scope.WorkspacePath))
            query = query.Where(m => m.WorkspacePath == scope.WorkspacePath);
        if (scope.FeatureId is { } featureId)
            query = query.Where(m => m.StageRun != null && m.StageRun.ReleaseFeatureId == featureId);
        if (scope.ReleaseId is { } releaseId)
            query = query.Where(m => m.StageRun != null && m.StageRun.Feature.ReleaseId == releaseId);
        return query;
    }

    private static IQueryable<ReleaseGateCheck> ScopedChecks(IQueryable<ReleaseGateCheck> query, MetricsScope scope)
    {
        query = query.Where(check => !check.Passed);
        if (!string.IsNullOrWhiteSpace(scope.WorkspacePath))
            query = query.Where(check => check.StageRun.Feature.Release.WorkspacePath == scope.WorkspacePath);
        if (scope.FeatureId is { } featureId)
            query = query.Where(check => check.StageRun.ReleaseFeatureId == featureId);
        if (scope.ReleaseId is { } releaseId)
            query = query.Where(check => check.StageRun.Feature.ReleaseId == releaseId);
        return query;
    }

    private static IReadOnlyList<PromptSectionMetrics> BuildPromptSections(IReadOnlyList<TurnMetric> metrics)
    {
        var sums = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var counted = 0;

        void Add(string section, int chars)
        {
            sums[section] = sums.GetValueOrDefault(section) + chars;
            total += chars;
        }

        foreach (var metric in metrics)
        {
            if (string.IsNullOrWhiteSpace(metric.PromptBreakdownJson))
                continue;

            try
            {
                var composition = JsonSerializer.Deserialize<PromptComposition>(metric.PromptBreakdownJson);
                if (composition is null)
                    continue;

                counted++;
                Add("base", composition.Base);
                Add("seed", composition.Seed);
                Add("profile", composition.Profile);
                Add("handoff", composition.Handoff);
                Add("delegation", composition.Delegation);
                Add("guidance", composition.Guidance);
                Add("points", composition.Points);
                Add("artifact", composition.Artifact);
                Add("interview", composition.Interview);
            }
            catch (JsonException)
            {
                // A malformed breakdown simply doesn't contribute.
            }
        }

        return sums
            .Select(pair => new PromptSectionMetrics(
                pair.Key,
                pair.Value,
                counted == 0 ? 0 : pair.Value / counted,
                total == 0 ? 0 : (double)pair.Value / total))
            .OrderByDescending(section => section.TotalChars)
            .ToList();
    }
}
