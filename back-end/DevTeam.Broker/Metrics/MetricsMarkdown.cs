using System.Text;

namespace DevTeam.Broker.Metrics;

/// <summary>
/// Renders a <see cref="MetricsSummary"/> as one concise Markdown report — for a person reading it
/// on screen, or an LLM reading it in a single call. Aggregates only, never the raw rows.
/// </summary>
public static class MetricsMarkdown
{
    public static string Render(MetricsSummary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# DevTeam efficiency report").AppendLine();
        builder.AppendLine($"- Generated: {summary.GeneratedAtUtc:u}");
        builder.AppendLine($"- Scope: workspace={summary.Scope.WorkspacePath ?? "(all)"}" +
                           $", feature={summary.Scope.FeatureId?.ToString() ?? "(all)"}" +
                           $", release={summary.Scope.ReleaseId?.ToString() ?? "(all)"}, last {summary.Scope.Days} day(s)");
        builder.AppendLine($"- Turns: {summary.Totals.Turns}, tokens: {summary.Totals.TotalTokens:N0} " +
                           $"(in {summary.Totals.InputTokens:N0} / out {summary.Totals.OutputTokens:N0}, cached {summary.Totals.CachedReadTokens:N0}), " +
                           $"time: {summary.Totals.DurationMs / 60_000} min" +
                           (summary.Totals.CostAmount is { } cost ? $", cost: {cost:0.####}" : string.Empty));
        builder.AppendLine();

        builder.AppendLine("## Findings").AppendLine();
        if (summary.Findings.Count == 0)
        {
            builder.AppendLine("No inefficiencies detected in this window.");
        }
        else
        {
            foreach (var finding in summary.Findings)
            {
                builder.AppendLine($"### [{finding.Severity}] {finding.Title}  (`{finding.Id}`)");
                builder.AppendLine($"- Evidence: {string.Join(", ", finding.Evidence.Select(e => $"{e.Key}={e.Value}"))}");
                builder.AppendLine($"- Suggested action: {finding.SuggestedAction}");
                builder.AppendLine();
            }
        }

        AppendTable(builder, "Tokens by stage",
            ["stage", "turns", "attempts", "tokens", "retry", "review", "minutes", "failed checks"],
            summary.PerStage.Select(s => new[] { s.StageName, s.Turns.ToString(), s.Attempts.ToString(), s.TotalTokens.ToString("N0"),
                s.RetryTokens.ToString("N0"), s.ChallengeTokens.ToString("N0"), (s.DurationMs / 60_000).ToString(), s.GateFailures.ToString() }));

        AppendTable(builder, "Tokens by cause",
            ["cause", "turns", "tokens", "minutes"],
            summary.PerKind.Select(k => new[] { k.Kind, k.Turns.ToString(), k.TotalTokens.ToString("N0"), (k.DurationMs / 60_000).ToString() }));

        AppendTable(builder, "Tokens by model",
            ["model", "turns", "tokens", "cached", "minutes"],
            summary.PerModel.Select(m => new[] { m.ModelId, m.Turns.ToString(), m.TotalTokens.ToString("N0"),
                m.CachedReadTokens.ToString("N0"), (m.DurationMs / 60_000).ToString() }));

        AppendTable(builder, "Prompt composition",
            ["section", "total chars", "avg chars", "% of prompt"],
            summary.PromptSections.Select(s => new[] { s.Section, s.TotalChars.ToString("N0"), s.AvgChars.ToString("N0"),
                (s.PercentOfPrompt * 100).ToString("0.#") + "%" }));

        builder.AppendLine("## Notes").AppendLine();
        foreach (var note in summary.Notes)
            builder.AppendLine($"- {note}");

        return builder.ToString();
    }

    private static void AppendTable(StringBuilder builder, string title, string[] headers, IEnumerable<string[]> rows)
    {
        builder.AppendLine().AppendLine($"## {title}").AppendLine();
        builder.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        builder.Append('|').Append(string.Join('|', headers.Select(_ => "---"))).AppendLine("|");
        foreach (var row in rows)
            builder.Append("| ").Append(string.Join(" | ", row)).AppendLine(" |");
    }
}
