import { featureProgress, folderSummary, relativeTime, sortFeatures } from "./releaseView";
import { featureStateLabel, folderKindLabel, releaseStateLabel } from "./terms";
import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

function run(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "r", releaseFeatureId: "f", stageName: "developer", status: "Active", phase: "Producing", questionCount: 0,
    attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false, gateChecks: [], findings: [],
    guidanceNotes: [], specialistConsultations: [], lastErrorKind: "None", ...o,
  };
}

function feat(o: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f", releaseId: "rel", key: "k", title: "k", branchName: "feature/k", status: "InProgress",
    createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", stageRuns: [], signoffs: [],
    flowPosition: { id: "fp", releaseFeatureId: "f", currentStageIndex: 1, currentStageName: "developer" }, ...o,
  };
}

function rel(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel", workspacePath: "D:\\apps\\calculator", title: "Release adding", version: "0.1.0", status: "InProgress",
    branchName: "release/adding", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    features: [], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

const PIPELINE = ["business-analyst", "developer", "qa"];

describe("wording", () => {
  it.each([
    ["Complete", "Done"], ["InProgress", "In progress"], ["OnHold", "Paused"], ["Proposed", "Not started"],
  ])("feature %s reads %s", (status, text) => expect(featureStateLabel(status)).toBe(text));

  it.each([
    ["Ready", "Ready to ship"], ["Released", "Shipped"], ["InProgress", "In progress"], ["Draft", "Draft"],
    ["Blocked", "Needs attention"], ["Escalated", "Needs attention"], ["Cancelled", "Cancelled"],
  ])("release %s reads %s", (status, text) => expect(releaseStateLabel(status)).toBe(text));

  it("calls a hotfix an urgent fix and a release a release", () => {
    expect(folderKindLabel(true)).toBe("Urgent fix");
    expect(folderKindLabel(false)).toBe("Release");
  });

  it("passes unknown values through rather than hiding them", () => {
    expect(featureStateLabel("Weird")).toBe("Weird");
    expect(releaseStateLabel("Weird")).toBe("Weird");
  });

  it("never shows git or internal vocabulary", () => {
    const all = ["Complete", "InProgress", "OnHold", "Proposed"].map(featureStateLabel)
      .concat(["Ready", "Released", "InProgress", "Draft", "Blocked", "Escalated"].map(releaseStateLabel))
      .concat([folderKindLabel(true), folderKindLabel(false)]).join(" ");
    expect(all).not.toMatch(/checkout|stash|branch|HEAD|gate|gherkin|BlockedSignoff|OnHold/i);
  });
});

describe("featureProgress", () => {
  it("names the current stage and where it sits in the pipeline", () => {
    expect(featureProgress(feat(), PIPELINE)).toEqual({ label: "Developer — stage 2 of 3", stageNumber: 2, stageCount: 3 });
  });

  it("says Done for a finished feature", () => {
    expect(featureProgress(feat({ status: "Complete" }), PIPELINE).label).toBe("Done");
  });

  it("says Not started when there is no position", () => {
    expect(featureProgress(feat({ status: "Proposed", flowPosition: null }), PIPELINE).label).toBe("Not started");
  });

  it("copes with a stage that isn't in the pipeline", () => {
    const p = featureProgress(feat({ flowPosition: { id: "x", releaseFeatureId: "f", currentStageIndex: 9, currentStageName: "mystery" } }), PIPELINE);
    expect(p.label).toBe("Mystery");
    expect(p.stageCount).toBe(3);
  });

  it("copes with an empty pipeline (not loaded yet)", () => {
    expect(featureProgress(feat(), []).label).toBe("Developer");
  });
});

describe("sortFeatures", () => {
  const needs = feat({ id: "needs", key: "needs", stageRuns: [run({ status: "BlockedSignoff" })] });
  const working = feat({ id: "working", key: "working", stageRuns: [run()] });
  const paused = feat({ id: "paused", key: "paused", status: "OnHold" });
  const fresh = feat({ id: "fresh", key: "fresh", status: "Proposed" });
  const done = feat({ id: "done", key: "done", status: "Complete" });

  it("orders needs-you, in progress, paused, not started, done", () => {
    expect(sortFeatures([done, fresh, paused, working, needs]).map((f) => f.id)).toEqual(["needs", "working", "paused", "fresh", "done"]);
  });

  it("is deterministic within a group: newest activity first, then key", () => {
    const a = feat({ id: "a", key: "alpha", updatedAt: "2026-01-02T00:00:00Z" });
    const b = feat({ id: "b", key: "beta", updatedAt: "2026-01-03T00:00:00Z" });
    const c = feat({ id: "c", key: "charlie", updatedAt: "2026-01-03T00:00:00Z" });
    expect(sortFeatures([a, c, b]).map((f) => f.id)).toEqual(["b", "c", "a"]);
    expect(sortFeatures([b, a, c]).map((f) => f.id)).toEqual(["b", "c", "a"]);
  });

  it("does not mutate its input", () => {
    const input = [done, needs];
    sortFeatures(input);
    expect(input.map((f) => f.id)).toEqual(["done", "needs"]);
  });
});

describe("folderSummary", () => {
  it("counts finished features and reports the state in plain words", () => {
    const s = folderSummary(rel({ features: [feat({ id: "a", status: "Complete" }), feat({ id: "b" })] }));
    expect(s).toMatchObject({ doneCount: 1, totalCount: 2, stateLabel: "In progress", needsYou: false });
  });

  it("says Ready to ship only when everything is done (the reported bug)", () => {
    const stale = rel({ status: "Ready", features: [feat({ id: "a", status: "Complete" }), feat({ id: "b", status: "InProgress" })] });
    expect(folderSummary(stale).stateLabel).toBe("In progress");
    const allDone = rel({ status: "Ready", features: [feat({ id: "a", status: "Complete" })] });
    expect(folderSummary(allDone).stateLabel).toBe("Ready to ship");
  });

  it("raises the folder's attention when any feature needs the user", () => {
    const s = folderSummary(rel({ features: [feat({ id: "a" }), feat({ id: "b", stageRuns: [run({ status: "BlockedGate" })] })] }));
    expect(s.needsYou).toBe(true);
    expect(s.attentionReason).toBe("Some checks didn't pass");
  });

  it("reports the most recent activity across the release, features and runs", () => {
    const s = folderSummary(rel({
      updatedAt: "2026-01-02T00:00:00Z",
      features: [feat({ updatedAt: "2026-01-03T00:00:00Z", stageRuns: [run({ finishedAt: "2026-01-05T10:00:00Z" })] })],
    }));
    expect(s.lastActivity).toBe("2026-01-05T10:00:00Z");
  });

  it("copes with a release that has no features", () => {
    expect(folderSummary(rel())).toMatchObject({ doneCount: 0, totalCount: 0, needsYou: false });
  });
});

describe("relativeTime", () => {
  const now = new Date("2026-01-10T12:00:00Z");
  it.each([
    ["2026-01-10T11:59:40Z", "just now"],
    ["2026-01-10T11:55:00Z", "5 minutes ago"],
    ["2026-01-10T11:59:00Z", "1 minute ago"],
    ["2026-01-10T10:00:00Z", "2 hours ago"],
    ["2026-01-10T11:00:00Z", "1 hour ago"],
    ["2026-01-07T12:00:00Z", "3 days ago"],
    ["2026-01-09T11:00:00Z", "1 day ago"],
  ])("%s → %s", (iso, text) => expect(relativeTime(iso, now)).toBe(text));

  it("shows a date for anything older than a month", () => {
    expect(relativeTime("2025-11-01T00:00:00Z", now)).toMatch(/2025/);
  });

  it("never says something nonsensical for a future or invalid time", () => {
    expect(relativeTime("2026-02-01T00:00:00Z", now)).toBe("just now");
    expect(relativeTime("not a date", now)).toBe("");
    expect(relativeTime(undefined, now)).toBe("");
  });
});
