using DevTeam.Broker.Domain;

namespace DevTeam.Broker.Metrics;

public sealed record MetricsScope(string? WorkspacePath, Guid? FeatureId, Guid? ReleaseId, int Days);

public sealed record MetricsTotals(
    int Turns, long InputTokens, long OutputTokens, long TotalTokens, long CachedReadTokens, long DurationMs, decimal? CostAmount);

public sealed record StageMetrics(
    string StageName, int Turns, int Attempts, long TotalTokens, long RetryTokens, long ChallengeTokens,
    long DurationMs, int GateFailures);

public sealed record KindMetrics(string Kind, int Turns, long TotalTokens, long DurationMs);

public sealed record ModelMetrics(string ModelId, int Turns, long TotalTokens, long CachedReadTokens, long DurationMs);

public sealed record PromptSectionMetrics(string Section, long TotalChars, long AvgChars, double PercentOfPrompt);

public sealed record MetricsFinding(
    string Id, string Severity, string Title, IReadOnlyDictionary<string, object?> Evidence, string SuggestedAction);

public sealed record MetricsSummary(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    MetricsScope Scope,
    MetricsTotals Totals,
    IReadOnlyList<StageMetrics> PerStage,
    IReadOnlyList<KindMetrics> PerKind,
    IReadOnlyList<ModelMetrics> PerModel,
    IReadOnlyList<PromptSectionMetrics> PromptSections,
    IReadOnlyList<MetricsFinding> Findings,
    IReadOnlyList<string> Notes);

public sealed record TurnMetricRow(
    DateTimeOffset StartedAt, string StageName, string? FeatureKey, string? ModelId, string Kind, int Attempt,
    long DurationMs, long? TimeToFirstEventMs, long InputTokens, long OutputTokens, long TotalTokens,
    long? CachedReadTokens, int PromptChars, int TextEvents, int ThoughtEvents, int ToolEvents,
    string? StopReason, string Outcome);
