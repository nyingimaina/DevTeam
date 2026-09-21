import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import { StageScreen } from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { MessageDto, PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: [], steps: ["scaffold_specs", "agent:business-analyst", "gherkin_validator"] },
  { name: "developer", userInputRequired: false, signoff: null, expectedArtifacts: [], steps: ["agent:developer", "verify_code"] },
];

function makeRun(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr1", releaseFeatureId: "f1", stageName: "business-analyst", status: "Active", phase: "GuidedQA", questionCount: 1,
    attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [],
    specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp-1", ...o,
  };
}

function makeRelease(run?: ReleaseStageRunDto): ReleaseDto {
  return {
    id: "rel1", workspacePath: "C:\\work\\proj", title: "Release", version: "0.1.0", status: "InProgress", branchName: "release/x",
    currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    features: [], stageRuns: run ? [run] : [], signoffs: [],
    flowPosition: { id: "fp", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" },
  };
}

const MESSAGES: MessageDto[] = [
  { id: "m1", role: "user", bodyText: "We need a login form", createdAt: "2026-01-01T00:00:00Z", parts: [], isPriming: false },
  { id: "m2", role: "assistant", bodyText: "Requirements settled.\nDONE", createdAt: "2026-01-01T00:00:01Z", parts: [], isPriming: false },
];

function renderStage(opts: { run?: ReleaseStageRunDto; stage?: number; readOnly?: boolean }) {
  const stageIndex = opts.stage ?? 0;
  const role = PIPELINE[stageIndex];
  const run = opts.run;
  return render(
    <StageScreen
      release={makeRelease(run)}
      featureId="f1"
      api={mockApi as unknown as BrokerApi}
      pipeline={PIPELINE}
      role={role}
      run={run}
      testIdPrefix="release"
      refreshRelease={async () => undefined}
      {...(opts.readOnly === undefined ? {} : { readOnly: opts.readOnly })}
    />,
  );
}

// Every call that would run the agent or change anything. Looking must never make one.
const STATE_CHANGING: (keyof BrokerApi)[] = [
  "startStageAsync", "runStageAsync", "sendStageMessageAsync", "runStageGatesAsync", "runStageGatesWithRepairAsync",
  "switchStageModelAsync", "setModelAsync", "pushBackAsync", "signoffFeatureAsync", "switchFeatureAsync", "createFeatureAsync",
];

function expectNothingChanged() {
  for (const name of STATE_CHANGING) {
    expect((mockApi as unknown as Record<string, jest.Mock>)[name]).not.toHaveBeenCalled();
  }
}

describe("StageScreen readOnly", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getStageMessagesAsync.mockResolvedValue(MESSAGES);
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getSessionAsync.mockResolvedValue({ modelId: "a/one" } as never);
  });

  describe("by default (prop omitted) nothing changes", () => {
    it("shows the chat input and the move-on control for an interactive stage", async () => {
      renderStage({ run: makeRun() });

      expect(await screen.findByTestId("release-chat-input")).toBeInTheDocument();
      expect(screen.getByTestId("release-send-btn")).toBeInTheDocument();
      expect(screen.getByTestId("release-move-on-btn")).toBeInTheDocument();
    });

    it("still auto-runs the checks when the agent says DONE", async () => {
      mockApi.runStageGatesWithRepairAsync.mockResolvedValue({
        release: makeRelease(makeRun()), outcome: "Passed", autoFixAttempts: 0, problems: [],
      });
      renderStage({ run: makeRun() });

      await waitFor(() => expect(mockApi.runStageGatesWithRepairAsync).toHaveBeenCalledWith("f1"));
    });
  });

  describe("with readOnly", () => {
    it("shows the conversation but offers no way to add to it", async () => {
      renderStage({ run: makeRun(), readOnly: true });

      expect(await screen.findByText("We need a login form")).toBeInTheDocument();
      expect(screen.queryByTestId("release-chat-input")).not.toBeInTheDocument();
      expect(screen.queryByTestId("release-send-btn")).not.toBeInTheDocument();
      expect(screen.queryByTestId("release-move-on-btn")).not.toBeInTheDocument();
    });

    it("does NOT auto-run the checks when the transcript ends in DONE", async () => {
      renderStage({ run: makeRun(), readOnly: true });

      await screen.findByText("We need a login form");
      await new Promise((r) => setTimeout(r, 50)); // give any (wrong) auto-run a chance to fire
      expectNothingChanged();
    });

    it("shows a finished/blocked autonomous stage's log without run, retry or push-back controls", async () => {
      const run = makeRun({
        id: "sr2", stageName: "developer", status: "BlockedGate", phase: "Gates",
        gateChecks: [{ id: "g1", stageRunId: "sr2", name: "verify_code", displayTitle: "The code builds and its tests pass", passed: false, evidenceText: "tests failed", isEntryGate: false }],
      });
      renderStage({ run, stage: 1, readOnly: true });

      expect(await screen.findByTestId("release-stage-log")).toBeInTheDocument();
      expect(screen.getByText(/The code builds and its tests pass/)).toBeInTheDocument();
      expect(screen.queryByTestId("release-run-stage-btn")).not.toBeInTheDocument();
      expect(screen.queryByTestId("release-pushback-btn")).not.toBeInTheDocument();
      expect(screen.queryByTestId("release-pushback-instructions")).not.toBeInTheDocument();
    });

    it("never starts the countdown that would re-run a failed stage on its own", async () => {
      const run = makeRun({ id: "sr2", stageName: "developer", status: "BlockedGate", phase: "Gates" });
      renderStage({ run, stage: 1, readOnly: true });

      await screen.findByTestId("release-stage-log");
      await new Promise((r) => setTimeout(r, 50));
      expect(screen.queryByText(/Running again in/i)).not.toBeInTheDocument();
      expectNothingChanged();
    });

    it("does not open the AI-model recovery pane for a refused stage", async () => {
      const run = makeRun({
        id: "sr2", stageName: "developer", status: "Escalated", lastErrorKind: "ProviderRejected",
        lastErrorMessage: "Error from provider (Console): free tier", lastErrorAt: "2026-01-01T00:00:05Z",
      });
      renderStage({ run, stage: 1, readOnly: true });

      await screen.findByTestId("release-stage-log");
      expect(screen.queryByTestId("release-model-problem")).not.toBeInTheDocument();
    });

    it("hides the AI engine picker", async () => {
      mockApi.getAvailableModelsAsync.mockResolvedValue([{ value: "a/one", name: "Model One", description: null }]);
      renderStage({ run: makeRun(), readOnly: true });

      await screen.findByText("We need a login form");
      expect(screen.queryByTestId("release-model-picker")).not.toBeInTheDocument();
    });

    it("says a stage hasn't started instead of offering to start it", async () => {
      renderStage({ run: undefined, readOnly: true });

      expect(await screen.findByText(/hasn.t started/i)).toBeInTheDocument();
      expect(screen.queryByTestId("release-start-stage-btn")).not.toBeInTheDocument();
      expect(screen.queryByText(/Run Stage|Start Conversation/i)).not.toBeInTheDocument();
    });

    it("still lets you open the diagnostics for a stage", async () => {
      renderStage({ run: makeRun({ status: "Complete" }), readOnly: true });

      expect(await screen.findByTestId("release-diagnostics-btn")).toBeInTheDocument();
    });
  });
});
