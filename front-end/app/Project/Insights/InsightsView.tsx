import React, { useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { MetricsSummaryDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/Insights.module.css";

interface IInsightsViewProps {
  api: BrokerApi;
  workspacePath: string;
}

/**
 * What DevTeam has been costing, and where it looks wasteful. Reads the same ranked findings an
 * agent would read from `/api/metrics/diagnose`, so a person and an LLM see the same thing.
 */
export default function InsightsView({ api, workspacePath }: IInsightsViewProps) {
  const [summary, setSummary] = useState<MetricsSummaryDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await api.getMetricsSummaryAsync(workspacePath);
        if (!cancelled) {
          setSummary(result ?? null);
          setError(null);
        }
      } catch {
        if (!cancelled) setError("Couldn't load the efficiency report.");
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [api, workspacePath]);

  if (error) {
    return (
      <div className={styles.empty} data-testid="insights-error">
        {error}
      </div>
    );
  }

  if (!summary) {
    return (
      <div className={styles.empty} data-testid="insights-loading">
        Loading…
      </div>
    );
  }

  const totals = summary.totals;
  const minutes = Math.round(totals.durationMs / 60_000);

  return (
    <section className={styles.view} data-testid="insights-view">
      <h2 className={styles.heading}>Efficiency</h2>
      <p className={styles.totals} data-testid="insights-totals">
        {totals.turns} turns · {totals.totalTokens.toLocaleString()} tokens (in {totals.inputTokens.toLocaleString()} / out{" "}
        {totals.outputTokens.toLocaleString()}) · {minutes} min
        {totals.costAmount != null ? ` · cost ${totals.costAmount}` : ""}
      </p>

      <h3 className={styles.subheading}>What looks wasteful</h3>
      {summary.findings.length === 0 ? (
        <p className={styles.ok} data-testid="insights-no-findings">
          Nothing stands out in this window.
        </p>
      ) : (
        <ul className={styles.findings} data-testid="insights-findings">
          {summary.findings.map((finding) => (
            <li key={finding.id} className={styles.finding} data-testid={`insight-${finding.id}`}>
              <span
                className={`${styles.severity} ${
                  finding.severity === "high" ? styles.high : finding.severity === "medium" ? styles.medium : styles.low
                }`}
              >
                {finding.severity}
              </span>
              <span className={styles.findingTitle}>{finding.title}</span>
              <div className={styles.action}>{finding.suggestedAction}</div>
            </li>
          ))}
        </ul>
      )}

      <h3 className={styles.subheading}>Cost by stage</h3>
      <table className={styles.table} data-testid="insights-stages">
        <thead>
          <tr>
            <th>Stage</th>
            <th>Turns</th>
            <th>Attempts</th>
            <th>Tokens</th>
            <th>Retries</th>
            <th>Review</th>
            <th>Minutes</th>
          </tr>
        </thead>
        <tbody>
          {summary.perStage.map((stage) => (
            <tr key={stage.stageName}>
              <td>{stage.stageName}</td>
              <td>{stage.turns}</td>
              <td>{stage.attempts}</td>
              <td>{stage.totalTokens.toLocaleString()}</td>
              <td>{stage.retryTokens.toLocaleString()}</td>
              <td>{stage.challengeTokens.toLocaleString()}</td>
              <td>{Math.round(stage.durationMs / 60_000)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <h3 className={styles.subheading}>What fills a prompt</h3>
      <ul className={styles.sections} data-testid="insights-sections">
        {summary.promptSections.map((section) => (
          <li key={section.section} className={styles.section}>
            <span className={styles.sectionLabel}>{section.section}</span>
            <span className={styles.bar}>
              <span className={styles.barFill} style={{ width: `${Math.round(section.percentOfPrompt * 100)}%` }} />
            </span>
            <span className={styles.sectionPct}>{Math.round(section.percentOfPrompt * 100)}%</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
