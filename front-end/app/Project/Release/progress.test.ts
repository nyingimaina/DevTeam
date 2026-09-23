import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";
import {
  estimateStageDurationMs,
  formatEstimate,
  historicalStageDurationsMs,
  overdueNote,
  requirementProgressBars,
  stepProgress,
} from "./progress";

describe("requirementProgressBars", () => {
  it("is hidden until a requirement list exists", () => {
    expect(requirementProgressBars(null).visible).toBe(false);
    expect(
      requirementProgressBars({ requirements: 0, code: { done: 0, total: 0 }, tests: { done: 0, total: 0 } }).visible,
    ).toBe(false);
  });

  it("turns done counts into ratios for code and tests", () => {
    const bars = requirementProgressBars({
      requirements: 4,
      code: { done: 2, total: 4 },
      tests: { done: 3, total: 4 },
    });

    expect(bars.visible).toBe(true);
    expect(bars.code).toEqual({ completed: 2, total: 4, ratio: 0.5 });
    expect(bars.tests).toEqual({ completed: 3, total: 4, ratio: 0.75 });
  });
});

function run(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr",
    releaseFeatureId: "f",
    stageName: "developer",
    status: "Complete",
    phase: "Signoff",
    questionCount: 0,
    attempt: 1,
    startedAt: "2026-01-01T00:00:00Z",
    finishedAt: "2026-01-01T00:10:00Z",
    readyToProceed: true,
    gateChecks: [],
    findings: [],
    guidanceNotes: [],
    specialistConsultations: [],
    lastErrorKind: "None",
    ...o,
  };
}

function feature(o: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f",
    releaseId: "r",
    key: "k",
    title: "k",
    branchName: "feature/k",
    status: "InProgress",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    stageRuns: [],
    signoffs: [],
    flowPosition: null,
    ...o,
  };
}

function release(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "r",
    workspacePath: "D:\\apps\\x",
    title: "Release",
    version: "1.0.0",
    status: "InProgress",
    branchName: "release/x",
    currentFeatureId: "f",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    features: [],
    stageRuns: [],
    signoffs: [],
    flowPosition: null,
    ...o,
  };
}

describe("stepProgress", () => {
  it("counts the steps that have a check row", () => {
    const progress = stepProgress(["a", "b", "c"], [{ name: "a" }, { name: "c" }]);

    expect(progress).toEqual({ completed: 2, total: 3, ratio: 2 / 3 });
  });

  it("is zero-safe with no steps", () => {
    expect(stepProgress([], [])).toEqual({ completed: 0, total: 0, ratio: 0 });
  });

  it("tolerates runs that have no checks yet", () => {
    expect(stepProgress(["a"], undefined).completed).toBe(0);
  });
});

describe("historicalStageDurationsMs", () => {
  it("collects finished runs for one stage across the release, oldest first", () => {
    const r = release({
      stageRuns: [run({ id: "a", startedAt: "2026-01-01T00:00:00Z", finishedAt: "2026-01-01T00:02:00Z" })],
      features: [
        feature({ stageRuns: [run({ id: "b", startedAt: "2026-01-01T00:00:00Z", finishedAt: "2026-01-01T00:05:00Z" })] }),
      ],
    });

    expect(historicalStageDurationsMs(r, "developer")).toEqual([120_000, 300_000]);
  });

  it("ignores unfinished runs, other stages, and duplicates", () => {
    const r = release({
      stageRuns: [
        run({ id: "a", stageName: "developer", finishedAt: null as unknown as string }),
        run({ id: "b", stageName: "qa", finishedAt: "2026-01-01T00:03:00Z" }),
      ],
      features: [feature({ stageRuns: [run({ id: "a", stageName: "developer", finishedAt: null as unknown as string })] })],
    });

    expect(historicalStageDurationsMs(r, "developer")).toEqual([]);
  });
});

describe("estimateStageDurationMs", () => {
  it("stays silent without history", () => {
    expect(estimateStageDurationMs([])).toBeNull();
  });

  it("weights recent runs more heavily", () => {
    expect(estimateStageDurationMs([60_000, 120_000], 0.5)).toBe(90_000);
  });
});

describe("formatEstimate", () => {
  it("reads naturally", () => {
    expect(formatEstimate(30_000)).toBe("under a minute");
    expect(formatEstimate(60_000)).toBe("about a minute");
    expect(formatEstimate(900_000)).toBe("about 15 minutes");
  });
});

describe("overdueNote", () => {
  it("says nothing while the run is within its usual range", () => {
    expect(overdueNote(60_000, 600_000)).toBeNull();
    expect(overdueNote(600_000, null)).toBeNull();
  });

  it("warns once the run has clearly outlasted its own history", () => {
    expect(overdueNote(1_000_000, 600_000)).toMatch(/longer than usual/);
  });
});
