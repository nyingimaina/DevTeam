"use client";
import React, { useCallback, useEffect, useState } from "react";
import {
  Bar,
  BarChart,
  CartesianGrid,
  Cell,
  Line,
  LineChart,
  Pie,
  PieChart,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { CheckDefinitionDto, ReadinessReportDto, ReleaseDto } from "../../Chat/Data/BrokerTypes";
import { toErrorMessage } from "../Release/ReleaseWizard";
import {
  checkStatusTone,
  coverageBars,
  estimateDurationMs,
  formatDuration,
  statusSlices,
  trendSeries,
} from "./charts";
import styles from "../Styles/Checks.module.css";

const TONE_CLASS: Record<"ok" | "bad" | "warn", string> = {
  ok: styles.toneOk,
  bad: styles.toneBad,
  warn: styles.toneWarn,
};

const SLICE_COLORS: Record<string, string> = {
  Passed: "#16a34a",
  Failed: "#dc2626",
  Skipped: "#ca8a04",
};

const COVERAGE_TARGET = 80;

export interface IChecksViewProps {
  api: BrokerApi;
  workspacePath: string;
}

export default function ChecksView({ api, workspacePath }: IChecksViewProps) {
  const [checks, setChecks] = useState<CheckDefinitionDto[]>([]);
  const [releases, setReleases] = useState<ReleaseDto[]>([]);
  const [report, setReport] = useState<ReadinessReportDto | null>(null);
  const [history, setHistory] = useState<ReadinessReportDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const release = releases[0] ?? null;
  const estimate = estimateDurationMs(history);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    void (async () => {
      try {
        const catalog = await api.getChecksAsync(workspacePath);
        if (cancelled) return;
        setChecks(catalog);

        const loaded = await api.listReleasesAsync(workspacePath);
        if (cancelled) return;
        const sorted = [...loaded].sort((a, b) => (b.createdAt ?? "").localeCompare(a.createdAt ?? ""));
        setReleases(sorted);

        if (sorted.length > 0) {
          const [latest, series] = await Promise.all([
            api.getReadinessAsync(sorted[0].id).catch(() => null),
            api.getReadinessHistoryAsync(sorted[0].id).catch(() => [] as ReadinessReportDto[]),
          ]);
          if (cancelled) return;
          setReport(latest);
          setHistory(series);
        } else {
          setReport(null);
          setHistory([]);
        }
      } catch (e) {
        if (!cancelled) setError(toErrorMessage(e));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [api, workspacePath]);

  const runChecks = useCallback(async () => {
    if (!release) return;
    setRunning(true);
    setError(null);
    try {
      const fresh = await api.runReadinessAsync(release.id);
      setReport(fresh);
      setHistory((prev) => [...prev, fresh]);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setRunning(false);
    }
  }, [api, release]);

  const bars = coverageBars(report);
  const slices = statusSlices(report);
  const trend = trendSeries(history);

  return (
    <div className={styles.view} data-testid="checks-view">
      <header className={styles.header}>
        <h2 className={styles.title}>Checks</h2>
        <p className={styles.intro}>
          Everything we make the project prove before it is allowed to ship.
        </p>
      </header>

      {error && (
        <div className={styles.error} data-testid="checks-error">
          {error}
        </div>
      )}

      {loading ? (
        <div className={styles.loading} data-testid="checks-loading">
          Loading the checks…
        </div>
      ) : (
        <>
          <section className={styles.section}>
            <h3 className={styles.sectionTitle}>Latest result</h3>

            {running ? (
              <div className={styles.running} data-testid="checks-running">
                <span className={styles.spinner} aria-hidden />
                <span>
                  Checking the project{estimate ? ` — this usually takes ${formatDuration(estimate)}` : "…"}
                </span>
              </div>
            ) : null}

            {report ? (
              <div
                className={`${styles.verdict} ${report.passed ? styles.verdictOk : styles.verdictBad}`}
                data-testid="checks-verdict"
              >
                <div className={styles.verdictLabel}>{report.passed ? "Everything passed" : "Some checks didn't pass"}</div>
                <div className={styles.verdictSub} data-testid="checks-summary">
                  {report.passed
                    ? `${report.checks.length} of ${report.checks.length} checks passed${
                        report.skippedCount > 0 ? ` (${report.skippedCount} not checked)` : ""
                      }.`
                    : report.blockerSummary}
                </div>
              </div>
            ) : (
              <div className={styles.muted} data-testid="checks-no-results">
                These checks haven&apos;t run for this project yet.
              </div>
            )}

            {release ? (
              <button
                type="button"
                className={styles.runButton}
                onClick={runChecks}
                disabled={running}
                data-testid="checks-run-btn"
              >
                {running ? "Checking…" : "Run the checks now"}
              </button>
            ) : (
              <div className={styles.muted} data-testid="checks-no-release">
                Start a release first, then its checks will appear here.
              </div>
            )}
          </section>

          {checks.length === 0 ? (
            <div className={styles.empty} data-testid="checks-empty">
              We couldn&apos;t find anything to check in this project yet.
            </div>
          ) : (
            <section className={styles.section}>
              <h3 className={styles.sectionTitle}>What we check here</h3>
              <div className={styles.catalog} data-testid="checks-catalog">
                {checks.map((check) => (
                  <article key={check.id} className={styles.catalogCard} data-testid={`check-catalog-${check.id}`}>
                    <div className={styles.catalogTitle}>{check.title}</div>
                    <div className={styles.catalogCategory}>{check.category}</div>
                    <p className={styles.catalogWhy}>{check.whyItMatters}</p>
                    <p className={styles.catalogHow}>
                      <span className={styles.catalogHowLabel}>If it fails: </span>
                      {check.howToFix}
                    </p>
                    <details className={styles.details}>
                      <summary>Technical details</summary>
                      <pre className={styles.pre}>{check.technical}</pre>
                    </details>
                  </article>
                ))}
              </div>
            </section>
          )}

          {report ? (
            <section className={styles.section}>
              <h3 className={styles.sectionTitle}>The checks in detail</h3>

              <div className={styles.charts}>
                {bars.length > 0 && (
                  <div className={styles.chartCard} data-testid="checks-coverage">
                    <div className={styles.chartTitle}>How much the tests cover</div>
                    <div className={styles.chartBody}>
                      <ResponsiveContainer width="100%" height="100%">
                        <BarChart data={bars} margin={{ top: 4, right: 8, bottom: 0, left: -20 }}>
                          <CartesianGrid strokeDasharray="3 3" vertical={false} />
                          <XAxis dataKey="name" tick={{ fontSize: 12 }} />
                          <YAxis domain={[0, 100]} tick={{ fontSize: 12 }} />
                          <Tooltip formatter={(value) => `${value}%`} />
                          <Bar dataKey="pct" radius={[4, 4, 0, 0]}>
                            {bars.map((bar) => (
                              <Cell
                                key={bar.name}
                                fill={bar.pct >= COVERAGE_TARGET ? "#16a34a" : bar.pct >= COVERAGE_TARGET / 2 ? "#ca8a04" : "#dc2626"}
                              />
                            ))}
                          </Bar>
                        </BarChart>
                      </ResponsiveContainer>
                    </div>
                  </div>
                )}

                {slices.length > 0 && (
                  <div className={styles.chartCard} data-testid="checks-status-chart">
                    <div className={styles.chartTitle}>How the checks ended</div>
                    <div className={styles.chartBody}>
                      <ResponsiveContainer width="100%" height="100%">
                        <PieChart>
                          <Pie data={slices} dataKey="value" nameKey="name" innerRadius={40} outerRadius={70}>
                            {slices.map((slice) => (
                              <Cell key={slice.name} fill={SLICE_COLORS[slice.name]} />
                            ))}
                          </Pie>
                          <Tooltip />
                        </PieChart>
                      </ResponsiveContainer>
                    </div>
                    <ul className={styles.legend}>
                      {slices.map((slice) => (
                        <li key={slice.name}>
                          <span className={styles.legendDot} style={{ background: SLICE_COLORS[slice.name] }} aria-hidden />
                          {slice.name}: {slice.value}
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </div>

              {trend.length >= 2 && (
                <div className={styles.chartCard} data-testid="checks-trend">
                  <div className={styles.chartTitle}>How this has gone over time</div>
                  <div className={styles.chartBody}>
                    <ResponsiveContainer width="100%" height="100%">
                      <LineChart data={trend} margin={{ top: 4, right: 8, bottom: 0, left: -20 }}>
                        <CartesianGrid strokeDasharray="3 3" vertical={false} />
                        <XAxis dataKey="label" tick={{ fontSize: 12 }} />
                        <YAxis allowDecimals={false} tick={{ fontSize: 12 }} />
                        <Tooltip />
                        <Line type="monotone" dataKey="passed" stroke="#16a34a" strokeWidth={2} name="Passed" />
                        <Line type="monotone" dataKey="failed" stroke="#dc2626" strokeWidth={2} name="Failed" />
                        <Line type="monotone" dataKey="skipped" stroke="#ca8a04" strokeWidth={2} name="Not checked" />
                      </LineChart>
                    </ResponsiveContainer>
                  </div>
                </div>
              )}

              <div className={styles.results} data-testid="checks-results">
                {report.checks.map((check) => (
                  <article
                    key={check.phaseId}
                    className={`${styles.resultCard} ${TONE_CLASS[checkStatusTone(check.status)]}`}
                    data-testid={`check-result-${check.phaseId}`}
                  >
                    <div className={styles.resultHead}>
                      <span className={styles.resultTitle}>{check.title}</span>
                      <span className={styles.resultStatus}>{check.status === "Skipped" ? "Not checked" : check.status}</span>
                    </div>
                    <div className={styles.resultReason}>{check.reason}</div>
                    {(check.metrics?.testsPassed != null || check.metrics?.lineCoverage != null) && (
                      <div className={styles.resultMetrics}>
                        {check.metrics.testsPassed != null && (
                          <span>{check.metrics.testsPassed} tests passed</span>
                        )}
                        {check.metrics.testsFailed != null && check.metrics.testsFailed > 0 && (
                          <span>{check.metrics.testsFailed} failed</span>
                        )}
                        {check.metrics.lineCoverage != null && <span>{check.metrics.lineCoverage}% covered</span>}
                      </div>
                    )}
                    {check.rawOutput && (
                      <details className={styles.details}>
                        <summary>Show the output</summary>
                        <pre className={styles.pre}>{check.rawOutput}</pre>
                      </details>
                    )}
                  </article>
                ))}
              </div>
            </section>
          ) : null}
        </>
      )}
    </div>
  );
}
