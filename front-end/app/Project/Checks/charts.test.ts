import { ReadinessCheckDto, ReadinessReportDto } from "../../Chat/Data/BrokerTypes";
import {
  coverageBars,
  coverageOf,
  estimateDurationMs,
  formatDuration,
  statusSlices,
  trendSeries,
} from "./charts";

function check(overrides: Partial<ReadinessCheckDto>): ReadinessCheckDto {
  return {
    phaseId: "x",
    title: "A check",
    status: "Passed",
    reason: "",
    durationMs: 10,
    metrics: {},
    ...overrides,
  };
}

function report(overrides: Partial<ReadinessReportDto>): ReadinessReportDto {
  return {
    id: "r1",
    scope: "release",
    passed: true,
    startedAt: "2026-01-01T00:00:00Z",
    durationMs: 1000,
    checks: [],
    failedCount: 0,
    skippedCount: 0,
    blockerSummary: "",
    ...overrides,
  };
}

describe("coverageOf", () => {
  it("averages the coverage measured by whichever checks reported it", () => {
    const r = report({
      checks: [
        check({ metrics: { lineCoverage: 80 } }),
        check({ metrics: { lineCoverage: 90 } }),
        check({ metrics: {} }),
      ],
    });

    expect(coverageOf(r, "line")).toBe(85);
  });

  it("is null when nothing measured coverage", () => {
    expect(coverageOf(report({ checks: [check({})] }), "line")).toBeNull();
    expect(coverageOf(null, "line")).toBeNull();
  });
});

describe("coverageBars", () => {
  it("only includes the measures that were reported", () => {
    const r = report({
      checks: [check({ metrics: { lineCoverage: 80, functionCoverage: 70 } })],
    });

    expect(coverageBars(r)).toEqual([
      { name: "Lines", pct: 80 },
      { name: "Functions", pct: 70 },
    ]);
  });
});

describe("statusSlices", () => {
  it("counts each status and drops the empty ones", () => {
    const r = report({
      checks: [
        check({ status: "Passed" }),
        check({ status: "Passed" }),
        check({ status: "Failed" }),
      ],
    });

    expect(statusSlices(r)).toEqual([
      { name: "Passed", value: 2 },
      { name: "Failed", value: 1 },
    ]);
  });
});

describe("trendSeries", () => {
  it("builds a point per run, labelled by version or run number", () => {
    const series = trendSeries([
      report({ releaseVersion: "1.0.0", checks: [check({ status: "Failed" })] }),
      report({ checks: [check({ status: "Passed" }), check({ status: "Passed" })] }),
    ]);

    expect(series).toEqual([
      { label: "v1.0.0", passed: 0, failed: 1, skipped: 0, coverage: null },
      { label: "Run 2", passed: 2, failed: 0, skipped: 0, coverage: null },
    ]);
  });

  it("carries the line coverage through", () => {
    const series = trendSeries([report({ checks: [check({ metrics: { lineCoverage: 72 } })] })]);

    expect(series[0].coverage).toBe(72);
  });
});

describe("estimateDurationMs", () => {
  it("is null until a run has been measured", () => {
    expect(estimateDurationMs([])).toBeNull();
    expect(estimateDurationMs([report({ durationMs: 0 })])).toBeNull();
  });

  it("weights the most recent runs most heavily", () => {
    // 1000 then 10000 with alpha 0.5 -> 0.5*10000 + 0.5*1000 = 5500
    expect(estimateDurationMs([report({ durationMs: 1000 }), report({ durationMs: 10000 })], 0.5)).toBe(5500);
  });

  it("returns the only measurement when there is just one", () => {
    expect(estimateDurationMs([report({ durationMs: 4200 })])).toBe(4200);
  });
});

describe("formatDuration", () => {
  it("reads naturally", () => {
    expect(formatDuration(8000)).toBe("8 seconds");
    expect(formatDuration(1000)).toBe("1 second");
    expect(formatDuration(60_000)).toBe("about a minute");
    expect(formatDuration(90_000)).toBe("about 2 minutes");
    expect(formatDuration(300_000)).toBe("about 5 minutes");
  });
});
