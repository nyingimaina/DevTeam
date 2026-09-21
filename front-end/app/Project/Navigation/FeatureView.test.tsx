import React from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import FeatureView from "./FeatureView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: [], steps: ["scaffold_specs", "agent:business-analyst"] },
  { name: "developer", userInputRequired: false, signoff: null, expectedArtifacts: [], steps: ["agent:developer", "verify_code"] },
  { name: "qa", userInputRequired: false, signoff: "release-approval", expectedArtifacts: [], steps: ["agent:qa"] },
];

function run(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr", releaseFeatureId: "f", stageName: "business-analyst", status: "Active", phase: "GuidedQA", questionCount: 1,
    attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [],
    specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp", ...o,
  };
}

function feature(o: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f1", releaseId: "rel", key: "subtraction", title: "subtraction", branchName: "feature/subtraction", status: "InProgress",
    createdAt: "2026-01-02T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z", stageRuns: [run({ releaseFeatureId: "f1" })], signoffs: [],
    flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" }, ...o,
  };
}

const ADDING = feature({
  id: "f0", key: "adding", title: "adding", status: "Complete",
  stageRuns: [
    run({ id: "a-ba", releaseFeatureId: "f0", stageName: "business-analyst", status: "Complete", phase: "Signoff" }),
    run({ id: "a-dev", releaseFeatureId: "f0", stageName: "developer", status: "Complete", phase: "Gates", acpSessionId: undefined }),
    run({ id: "a-qa", releaseFeatureId: "f0", stageName: "qa", status: "Complete", phase: "Signoff", acpSessionId: undefined }),
  ],
  flowPosition: { id: "fp0", releaseFeatureId: "f0", currentStageIndex: 3, currentStageName: "done" },
});
const SUBTRACTION = feature();
const MULTIPLICATION = feature({
  id: "f2", key: "multiplication", title: "multiplication", status: "OnHold",
  stageRuns: [run({ id: "m-ba", releaseFeatureId: "f2" })],
  flowPosition: { id: "fp2", releaseFeatureId: "f2", currentStageIndex: 0, currentStageName: "business-analyst" },
});
const FRESH = feature({ id: "f3", key: "division", title: "division", status: "Proposed", stageRuns: [], flowPosition: null });

function release(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel", workspacePath: "D:\\apps\\calculator", title: "Release adding", version: "0.1.0", status: "Ready", branchName: "release/adding",
    currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
    features: [ADDING, SUBTRACTION, MULTIPLICATION, FRESH], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

const STATE_CHANGING = [
  "startStageAsync", "runStageAsync", "sendStageMessageAsync", "runStageGatesAsync", "runStageGatesWithRepairAsync",
  "switchStageModelAsync", "setModelAsync", "pushBackAsync", "signoffFeatureAsync", "switchFeatureAsync", "createFeatureAsync",
];

function expectNothingChanged(except: string[] = []) {
  for (const name of STATE_CHANGING.filter((n) => !except.includes(n))) {
    expect((mockApi as unknown as Record<string, jest.Mock>)[name]).not.toHaveBeenCalled();
  }
}

function renderView(featureId: string, rel: ReleaseDto = release(), onReleaseUpdated = jest.fn()) {
  const utils = render(
    <FeatureView api={mockApi as unknown as BrokerApi} release={rel} featureId={featureId} onReleaseUpdated={onReleaseUpdated} />,
  );
  return { ...utils, onReleaseUpdated };
}

describe("FeatureView", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getPipelineAsync.mockResolvedValue(PIPELINE);
    mockApi.getReleaseAsync.mockResolvedValue(release());
    mockApi.getStageMessagesAsync.mockResolvedValue([
      { id: "m1", role: "user", bodyText: "Add subtraction please", createdAt: "2026-01-02T00:00:00Z", parts: [], isPriming: false },
    ]);
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getSessionAsync.mockResolvedValue({ modelId: "a/one" } as never);
  });

  it("waits for the pipeline before drawing a stage (no flash of 'Release complete')", () => {
    mockApi.getPipelineAsync.mockReturnValue(new Promise(() => undefined));
    renderView("f1");

    expect(screen.getByTestId("release-feature-loading")).toBeInTheDocument();
    expect(screen.queryByText(/Release complete/i)).not.toBeInTheDocument();
  });

  describe("the active, in-progress feature", () => {
    it("shows its live workflow at its current stage — even though the release is stored as Ready", async () => {
      renderView("f1"); // the reported scenario: 'adding' done, 'subtraction' now in progress

      expect(await screen.findByTestId("release-chat-input")).toBeInTheDocument();
      expect(screen.getByText(/Current Stage: Business Analyst/i)).toBeInTheDocument();
      expect(screen.queryByText(/Release complete/i)).not.toBeInTheDocument();
      expect(screen.queryByTestId("release-feature-banner")).not.toBeInTheDocument();
    });
  });

  describe("a finished feature", () => {
    it("opens as a read-only record of its last stage, with history", async () => {
      renderView("f0");

      expect(await screen.findByTestId("release-feature-banner")).toHaveTextContent(/done/i);
      expect(await screen.findByTestId("release-stage-log")).toBeInTheDocument();
      expect(screen.getByText(/Current Stage: QA/i)).toBeInTheDocument();
      expect(screen.getByText("Stage history")).toBeInTheDocument();
      expect(screen.queryByTestId("release-run-stage-btn")).not.toBeInTheDocument();
      expect(screen.queryByTestId("release-resume-feature-btn")).not.toBeInTheDocument();
    });

    it("makes no request that changes anything", async () => {
      renderView("f0");
      await screen.findByTestId("release-stage-log");
      await new Promise((r) => setTimeout(r, 50));

      expectNothingChanged();
    });
  });

  describe("a paused feature", () => {
    it("shows its workflow read-only with a banner and a resume action", async () => {
      renderView("f2");

      const banner = await screen.findByTestId("release-feature-banner");
      expect(banner).toHaveTextContent(/paused/i);
      expect(screen.getByTestId("release-resume-feature-btn")).toHaveTextContent("Resume work here");
      expect(await screen.findByText("Add subtraction please")).toBeInTheDocument();
      expect(screen.queryByTestId("release-chat-input")).not.toBeInTheDocument();
    });

    it("makes no state-changing request just by being opened", async () => {
      renderView("f2");
      await screen.findByTestId("release-feature-banner");
      await new Promise((r) => setTimeout(r, 50));

      expectNothingChanged();
    });

    it("resumes only when asked, then hands the fresh release to the parent", async () => {
      const fresh = release({ currentFeatureId: "f2" });
      mockApi.switchFeatureAsync.mockResolvedValue(fresh);
      const { onReleaseUpdated } = renderView("f2");

      fireEvent.click(await screen.findByTestId("release-resume-feature-btn"));

      await waitFor(() => expect(mockApi.switchFeatureAsync).toHaveBeenCalledWith("f2"));
      expect(mockApi.switchFeatureAsync).toHaveBeenCalledTimes(1);
      await waitFor(() => expect(onReleaseUpdated).toHaveBeenCalledWith(fresh));
    });

    it("says so in plain words when resuming fails, and stays put", async () => {
      mockApi.switchFeatureAsync.mockRejectedValue(new Error("boom"));
      renderView("f2");

      fireEvent.click(await screen.findByTestId("release-resume-feature-btn"));

      expect(await screen.findByRole("alert")).toHaveTextContent(/couldn't resume/i);
      expect(screen.getByTestId("release-feature-banner")).toBeInTheDocument();
    });
  });

  describe("a feature that hasn't started", () => {
    it("says so, offers to start it, and shows no controls that run anything", async () => {
      renderView("f3");

      const banner = await screen.findByTestId("release-feature-banner");
      expect(banner).toHaveTextContent(/hasn.t started/i);
      expect(screen.getByTestId("release-resume-feature-btn")).toHaveTextContent("Start working here");
      expect(screen.queryByTestId("release-start-stage-btn")).not.toBeInTheDocument();
      expectNothingChanged();
    });
  });

  describe("an unknown feature", () => {
    it("shows a friendly message and a way back to the release", async () => {
      renderView("nope");

      expect(await screen.findByTestId("release-feature-not-found")).toHaveTextContent(/couldn't find that feature/i);
      expect(screen.getByRole("link", { name: /back to the release/i })).toHaveAttribute("href", "#/r/rel");
    });
  });

  describe("wording", () => {
    it("never shows git or internal vocabulary in its banners", async () => {
      const texts: string[] = [];
      for (const id of ["f0", "f2", "f3"]) {
        const { unmount } = renderView(id);
        texts.push((await screen.findByTestId("release-feature-banner")).textContent ?? "");
        unmount();
      }
      expect(texts.join(" ")).not.toMatch(/checkout|stash|branch|HEAD|gate|OnHold|Proposed/i);
    });
  });

  it("keeps polling the release so the view stays current, and reports what it gets", async () => {
    jest.useFakeTimers();
    try {
      const fresh = release({ updatedAt: "2026-02-01T00:00:00Z" });
      mockApi.getReleaseAsync.mockResolvedValue(fresh);
      const { onReleaseUpdated } = renderView("f1");

      await waitFor(() => expect(screen.getByTestId("release-chat-input")).toBeInTheDocument());
      jest.advanceTimersByTime(4000);

      await waitFor(() => expect(onReleaseUpdated).toHaveBeenCalledWith(fresh));
    } finally {
      jest.useRealTimers();
    }
  });
});
