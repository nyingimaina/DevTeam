import { ReadinessReportDto } from "../../Chat/Data/BrokerTypes";

export interface CoverageBar {
  name: string;
  pct: number;
}

export interface StatusSlice {
  name: "Passed" | "Failed" | "Skipped";
  value: number;
}

export interface TrendPoint {
  label: string;
  passed: number;
  failed: number;
  skipped: number;
  coverage: number | null;
}

const PASSED = "Passed";
const FAILED = "Failed";
const SKIPPED = "Skipped";

// Coverage is reported per check (only the test phases produce it), so the headline number is
// the average across whichever checks actually measured it.
export function coverageOf(report: ReadinessReportDto | null, kind: "line" | "branch" | "function"): number | null {
  if (!report) return null;
  const key = kind === "line" ? "lineCoverage" : kind === "branch" ? "branchCoverage" : "functionCoverage";
  const values = report.checks
    .map((check) => check.metrics?.[key])
    .filter((value): value is number => typeof value === "number");
  if (values.length === 0) return null;
  return Math.round((values.reduce((sum, value) => sum + value, 0) / values.length) * 10) / 10;
}

export function coverageBars(report: ReadinessReportDto | null): CoverageBar[] {
  const bars: CoverageBar[] = [];
  const line = coverageOf(report, "line");
  const branch = coverageOf(report, "branch");
  const funcs = coverageOf(report, "function");
  if (line !== null) bars.push({ name: "Lines", pct: line });
  if (branch !== null) bars.push({ name: "Branches", pct: branch });
  if (funcs !== null) bars.push({ name: "Functions", pct: funcs });
  return bars;
}

export function statusSlices(report: ReadinessReportDto | null): StatusSlice[] {
  if (!report) return [];
  return [
    { name: PASSED, value: report.checks.filter((c) => c.status === PASSED).length },
    { name: FAILED, value: report.checks.filter((c) => c.status === FAILED).length },
    { name: SKIPPED, value: report.checks.filter((c) => c.status === SKIPPED).length },
  ].filter((slice) => slice.value > 0) as StatusSlice[];
}

export function trendSeries(history: ReadinessReportDto[]): TrendPoint[] {
  return history.map((report, index) => ({
    label: report.releaseVersion ? `v${report.releaseVersion}` : `Run ${index + 1}`,
    passed: report.checks.filter((c) => c.status === PASSED).length,
    failed: report.checks.filter((c) => c.status === FAILED).length,
    skipped: report.checks.filter((c) => c.status === SKIPPED).length,
    coverage: coverageOf(report, "line"),
  }));
}

// A run's duration is a good predictor of the next one, so weight recent runs more heavily
// (exponential moving average). Null until there is at least one measured run — better to say
// nothing than to guess.
export function estimateDurationMs(history: ReadinessReportDto[], alpha = 0.4): number | null {
  const durations = history.map((report) => report.durationMs).filter((value) => value > 0);
  if (durations.length === 0) return null;

  let estimate = durations[0];
  for (let i = 1; i < durations.length; i += 1) {
    estimate = alpha * durations[i] + (1 - alpha) * estimate;
  }
  return Math.round(estimate);
}

export function formatDuration(ms: number): string {
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds} second${seconds === 1 ? "" : "s"}`;
  const minutes = Math.round(seconds / 60);
  return minutes === 1 ? "about a minute" : `about ${minutes} minutes`;
}

export function checkStatusTone(status: string): "ok" | "bad" | "warn" {
  if (status === FAILED) return "bad";
  if (status === SKIPPED) return "warn";
  return "ok";
}
