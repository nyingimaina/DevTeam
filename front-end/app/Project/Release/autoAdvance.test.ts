import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";
import { autoAdvanceStopReason, latestRun, nextAutoStep } from "./autoAdvance";

function stage(name: string, userInputRequired = false): PipelineStageDto {
  return { name, userInputRequired } as PipelineStageDto;
}

const pipeline = [stage("business-analyst", true), stage("developer"), stage("qa"), stage("verification")];

function run(stageName: string, status: string, attempt = 1): ReleaseStageRunDto {
  return { id: `${stageName}-${attempt}`, stageName, status, attempt, phase: "", questionCount: 0, readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [] } as unknown as ReleaseStageRunDto;
}

function release(overrides: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "r1",
    workspacePath: "D:\\apps",
    version: "0.1.0",
    status: "InProgress",
    branchName: "release/addition",
    createdAt: "",
    updatedAt: "",
    features: [],
    stageRuns: [],
    signoffs: [],
    flowPosition: { id: "f", releaseFeatureId: "x", currentStageIndex: 1, currentStageName: "developer" },
    ...overrides,
  } as ReleaseDto;
}

const signoff = (stageName: string, approved = false) =>
  ({ id: stageName, releaseFeatureId: "x", stageName, required: true, approved }) as ReleaseDto["signoffs"][number];

describe("latestRun", () => {
  it("picks the highest attempt for a stage", () => {
    const picked = latestRun([run("developer", "BlockedGate", 1), run("developer", "Active", 2)], "developer");
    expect(picked?.attempt).toBe(2);
  });
});

describe("nextAutoStep", () => {
  it("runs an autonomous stage that has no run yet", () => {
    expect(nextAutoStep(release(), pipeline)).toEqual({ kind: "run-stage" });
  });

  it("does not run the interactive first stage", () => {
    const r = release({ flowPosition: { id: "f", releaseFeatureId: "x", currentStageIndex: 0, currentStageName: "business-analyst" } });
    expect(nextAutoStep(r, pipeline)).toEqual({ kind: "wait", reason: "interactive" });
  });

  it("approves a stage that is only waiting for signoff", () => {
    const r = release({
      stageRuns: [run("developer", "BlockedSignoff")],
      signoffs: [signoff("developer")],
    });
    expect(nextAutoStep(r, pipeline)).toEqual({ kind: "signoff", stageName: "developer" });
  });

  it("does not re-approve an already approved stage", () => {
    const r = release({
      stageRuns: [run("developer", "Active")],
      signoffs: [signoff("developer", true)],
    });
    expect(nextAutoStep(r, pipeline)).toEqual({ kind: "wait", reason: "busy" });
  });

  it("stops when a stage needs a human", () => {
    const r = release({ stageRuns: [run("developer", "BlockedGate")] });
    expect(nextAutoStep(r, pipeline)).toEqual({ kind: "wait", reason: "blocked" });
  });

  it("waits while a stage is still working", () => {
    const r = release({ stageRuns: [run("developer", "GatesRunning")] });
    expect(nextAutoStep(r, pipeline)).toEqual({ kind: "wait", reason: "busy" });
  });

  it("is done once the flow is finished", () => {
    const r = release({
      effectiveStatus: "Ready",
      flowPosition: { id: "f", releaseFeatureId: "x", currentStageIndex: 4, currentStageName: "done" },
    });
    expect(nextAutoStep(r, pipeline)).toEqual({ kind: "wait", reason: "done" });
  });

  it("is a no-op without a release", () => {
    expect(nextAutoStep(undefined, pipeline)).toEqual({ kind: "wait", reason: "not-ready" });
  });
});

describe("autoAdvanceStopReason", () => {
  it("explains each stop in plain words", () => {
    expect(autoAdvanceStopReason("done")).toMatch(/all stages are done/i);
    expect(autoAdvanceStopReason("blocked")).toMatch(/attention/i);
    expect(autoAdvanceStopReason("interactive")).toMatch(/waiting for you/i);
    expect(autoAdvanceStopReason("busy")).toMatch(/continuing/i);
  });

  it("shows no internal jargon", () => {
    const all = (["interactive", "blocked", "busy", "done", "not-ready"] as const)
      .map(autoAdvanceStopReason)
      .join(" ");
    expect(all).not.toMatch(/gate|signoff|run-stage|status/i);
  });
});
