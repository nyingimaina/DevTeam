import { attentionFor, effectiveReleaseStatus, releaseForFeature } from "./releaseView";
import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

function run(overrides: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "r1", releaseFeatureId: "f1", stageName: "developer", status: "Active", phase: "Producing",
    questionCount: 0, attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false,
    gateChecks: [], findings: [], guidanceNotes: [], specialistConsultations: [], lastErrorKind: "None",
    ...overrides,
  };
}

function feature(overrides: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f1", releaseId: "rel", key: "subtraction", title: "subtraction", branchName: "feature/subtraction",
    status: "InProgress", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    stageRuns: [], signoffs: [], flowPosition: { id: "fp", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
    ...overrides,
  };
}

function release(overrides: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel", workspacePath: "D:\\apps\\calculator", title: "Release adding", version: "0.1.0", status: "Ready",
    branchName: "release/adding", currentFeatureId: null, createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    features: [], stageRuns: [], signoffs: [], flowPosition: null,
    ...overrides,
  };
}

describe("effectiveReleaseStatus", () => {
  it("prefers the broker's derived value", () => {
    expect(effectiveReleaseStatus(release({ status: "Ready", effectiveStatus: "InProgress" }))).toBe("InProgress");
  });

  it("reproduces the reported bug: stored Ready + a new in-progress feature is not 'Ready'", () => {
    const r = release({ features: [feature({ id: "a", status: "Complete" }), feature({ id: "b", status: "InProgress" })] });
    expect(effectiveReleaseStatus(r)).toBe("InProgress");
  });

  it("stays Ready when every feature is complete", () => {
    const r = release({ features: [feature({ id: "a", status: "Complete" })] });
    expect(effectiveReleaseStatus(r)).toBe("Ready");
  });

  it("treats a Ready release with no features as not ready to ship", () => {
    expect(effectiveReleaseStatus(release({ features: [] }))).toBe("InProgress");
  });

  it.each(["Released", "Cancelled", "Draft", "InProgress", "Blocked"])("leaves %s alone", (status) => {
    expect(effectiveReleaseStatus(release({ status, features: [feature({ status: "InProgress" })] }))).toBe(status);
  });
});

describe("releaseForFeature", () => {
  const done = feature({ id: "done", key: "adding", status: "Complete", stageRuns: [run({ id: "x", releaseFeatureId: "done", stageName: "qa", status: "Complete" })],
    flowPosition: { id: "fp0", releaseFeatureId: "done", currentStageIndex: 2, currentStageName: "qa" } });
  const active = feature({ id: "active", key: "subtraction", stageRuns: [run({ id: "y", releaseFeatureId: "active" })] });

  it("re-points the current-feature proxies at the chosen feature", () => {
    const r = release({ currentFeatureId: "active", features: [done, active], stageRuns: active.stageRuns!, flowPosition: active.flowPosition });

    const view = releaseForFeature(r, "done");

    expect(view.currentFeatureId).toBe("done");
    expect(view.stageRuns).toBe(done.stageRuns);
    expect(view.flowPosition).toBe(done.flowPosition);
    expect(view.signoffs).toEqual([]);
  });

  it("does not mutate the release it was given", () => {
    const r = release({ currentFeatureId: "active", features: [done, active], stageRuns: active.stageRuns! });
    const before = JSON.stringify(r);

    releaseForFeature(r, "done");

    expect(JSON.stringify(r)).toBe(before);
  });

  it("returns the release unchanged for an unknown feature", () => {
    const r = release({ features: [done] });
    expect(releaseForFeature(r, "nope")).toBe(r);
  });

  it("never reports 'Ready' for a feature view — that banner belongs to the folder", () => {
    const r = release({ status: "Ready", features: [done] });
    expect(releaseForFeature(r, "done").status).not.toBe("Ready");
  });

  it("copes with a feature that has no runs, signoffs or position yet", () => {
    const bare = feature({ id: "bare", stageRuns: undefined, signoffs: undefined, flowPosition: undefined });
    const view = releaseForFeature(release({ features: [bare] }), "bare");
    expect(view.stageRuns).toEqual([]);
    expect(view.signoffs).toEqual([]);
    expect(view.flowPosition).toBeNull();
  });
});

describe("attentionFor", () => {
  it.each([
    ["BlockedSignoff", "Needs your approval"],
    ["BlockedGate", "Some checks didn't pass"],
    ["BlockedEntry", "The previous step needs attention"],
  ])("flags a %s run as needing the user", (status, text) => {
    const a = attentionFor(feature({ stageRuns: [run({ status })] }));
    expect(a).toEqual({ level: "needs-you", reason: text });
  });

  it("explains a provider refusal", () => {
    const a = attentionFor(feature({ stageRuns: [run({ status: "Escalated", lastErrorKind: "ProviderRejected" })] }));
    expect(a).toEqual({ level: "needs-you", reason: "The AI service needs attention" });
  });

  it("explains another kind of agent error", () => {
    const a = attentionFor(feature({ stageRuns: [run({ status: "Escalated", lastErrorKind: "Disconnected" })] }));
    expect(a.level).toBe("needs-you");
    expect(a.reason).toMatch(/retry/i);
  });

  it("flags a run that is ready and waiting on an unapproved signoff", () => {
    const f = feature({
      stageRuns: [run({ status: "Active", readyToProceed: true })],
      signoffs: [{ id: "s", releaseFeatureId: "f1", stageName: "developer", required: true, approved: false }],
    });
    expect(attentionFor(f)).toEqual({ level: "needs-you", reason: "Needs your approval" });
  });

  it("has nothing to say about a healthy active run", () => {
    expect(attentionFor(feature({ stageRuns: [run({ status: "Active" })] }))).toEqual({ level: "none" });
  });

  it("has nothing to say about a finished feature, whatever its old runs did", () => {
    expect(attentionFor(feature({ status: "Complete", stageRuns: [run({ status: "BlockedGate" })] }))).toEqual({ level: "none" });
  });

  it("looks at the newest attempt, not an old failed one", () => {
    const f = feature({ stageRuns: [run({ id: "old", attempt: 1, status: "BlockedGate" }), run({ id: "new", attempt: 2, status: "Active" })] });
    expect(attentionFor(f)).toEqual({ level: "none" });
  });

  it("handles a feature with no runs", () => {
    expect(attentionFor(feature({ stageRuns: undefined }))).toEqual({ level: "none" });
  });

  it("never leaks internal status names in its wording", () => {
    const texts = ["BlockedSignoff", "BlockedGate", "BlockedEntry", "Escalated"].map((s) => attentionFor(feature({ stageRuns: [run({ status: s })] })).reason ?? "");
    expect(texts.join(" ")).not.toMatch(/Blocked|Escalated|gate|signoff/i);
  });
});
