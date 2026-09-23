namespace DevTeam.Broker.Metrics;

/// <summary>
/// Turns a <see cref="MetricsSummary"/> into ranked, plain-language findings — the part an LLM (or
/// a person) reads to decide what to fix. Pure and deterministic, so every rule is unit-tested and
/// a finding id means the same thing every run.
/// </summary>
public static class MetricsDiagnostics
{
    public const int MinTokensForShareFindings = 20_000;
    public const int MinTokensForCacheFindings = 100_000;
    public const double SlowStageMinutes = 10;

    public static IReadOnlyList<MetricsFinding> Findings(MetricsSummary summary)
    {
        var findings = new List<MetricsFinding>();
        var totalTokens = Math.Max(1, summary.Totals.TotalTokens);

        // Retries / corrections dominating a stage's spend.
        foreach (var stage in summary.PerStage)
        {
            if (stage.TotalTokens < MinTokensForShareFindings)
                continue;

            var retryShare = (double)stage.RetryTokens / stage.TotalTokens;
            if (retryShare > 0.25)
            {
                findings.Add(new MetricsFinding(
                    "retry-token-share", "high",
                    $"{Pct(retryShare)} of the {stage.StageName} stage's tokens were retries/corrections",
                    new Dictionary<string, object?>
                    {
                        ["stage"] = stage.StageName, ["retryTokens"] = stage.RetryTokens,
                        ["stageTokens"] = stage.TotalTokens, ["attempts"] = stage.Attempts,
                    },
                    "Fix whatever check keeps failing before running the stage again — the retries are the cost."));
            }

            var challengeShare = (double)stage.ChallengeTokens / stage.TotalTokens;
            if (challengeShare > 0.3)
            {
                findings.Add(new MetricsFinding(
                    "challenge-token-share", "medium",
                    $"{Pct(challengeShare)} of the {stage.StageName} stage's tokens were the review",
                    new Dictionary<string, object?>
                    {
                        ["stage"] = stage.StageName, ["reviewTokens"] = stage.ChallengeTokens, ["stageTokens"] = stage.TotalTokens,
                    },
                    "Weigh the review's value against its cost; consider a cheaper model for the antagonist or a shorter review prompt."));
            }

            if (stage.Attempts >= 3 && stage.GateFailures > 0)
            {
                findings.Add(new MetricsFinding(
                    "gate-thrash", "high",
                    $"The {stage.StageName} stage ran {stage.Attempts} times against {stage.GateFailures} failed check(s)",
                    new Dictionary<string, object?> { ["stage"] = stage.StageName, ["attempts"] = stage.Attempts, ["gateFailures"] = stage.GateFailures },
                    "A check that keeps failing is usually a false alarm or an unsatisfiable ask — inspect the check, not the agent."));
            }

            if (stage.DurationMs > SlowStageMinutes * 60_000)
            {
                findings.Add(new MetricsFinding(
                    "slow-stage", "low",
                    $"The {stage.StageName} stage spent {stage.DurationMs / 60_000} minutes",
                    new Dictionary<string, object?> { ["stage"] = stage.StageName, ["durationMs"] = stage.DurationMs },
                    "Check thought-heavy turns below; a long review or a stalling provider usually shows up here."));
            }
        }

        // Prompt bloat, by section.
        var artifact = summary.PromptSections.FirstOrDefault(section => section.Section == "artifact");
        if (artifact is not null && artifact.PercentOfPrompt > 0.5)
        {
            findings.Add(new MetricsFinding(
                "prompt-artifact-heavy", "high",
                $"{Pct(artifact.PercentOfPrompt)} of every prompt is the artifact context (BRS/context.md)",
                new Dictionary<string, object?> { ["artifactChars"] = artifact.TotalChars, ["percentOfPrompt"] = artifact.PercentOfPrompt },
                "Send a condensed requirement list with a pointer to the file, instead of the whole document on every turn."));
        }

        var guidance = summary.PromptSections.FirstOrDefault(section => section.Section == "guidance");
        if (guidance is not null && guidance.PercentOfPrompt > 0.2)
        {
            findings.Add(new MetricsFinding(
                "guidance-heavy", "medium",
                $"{Pct(guidance.PercentOfPrompt)} of every prompt is accumulated feedback notes",
                new Dictionary<string, object?> { ["guidanceChars"] = guidance.TotalChars, ["percentOfPrompt"] = guidance.PercentOfPrompt },
                "Only inject open/unresolved notes, and cap them — today every historical note is re-sent."));
        }

        // Prompt caching not helping.
        if (summary.Totals.TotalTokens >= MinTokensForCacheFindings && summary.Totals.CachedReadTokens == 0)
        {
            findings.Add(new MetricsFinding(
                "no-cache", "medium",
                $"{summary.Totals.TotalTokens:N0} tokens spent with no cached reads",
                new Dictionary<string, object?> { ["totalTokens"] = summary.Totals.TotalTokens, ["cachedReadTokens"] = 0 },
                "Repeated prompts should hit the provider's prompt cache; check whether the prompt changes every turn."));
        }

        return findings
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "high" => 3,
        "medium" => 2,
        _ => 1,
    };

    private static string Pct(double ratio) => $"{Math.Round(ratio * 100)}%";
}
