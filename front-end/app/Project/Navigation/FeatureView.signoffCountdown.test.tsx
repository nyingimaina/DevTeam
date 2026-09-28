import React from "react";
import { act, render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import FeatureView from "./FeatureView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: [], steps: ["agent:business-analyst"] },
  { name: "developer", userInputRequired: false, signoff: "pr-created", expectedArtifacts: [], steps: ["agent:developer"] },
];

const SIGN_OFF_SECONDS = 45;

function run(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr", releaseFeatureId: "f1", stageName: "business-analyst", status: "BlockedSignoff", phase: "Signoff", questionCount: 1,
    attempt: 1, startedAt: "2026-01-02T00:00:00Z", readyToProceed: true, gateChecks: [], findings: [], guidanceNotes: [],
    specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp", ...o,
  };
}

function feature(o: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f1", releaseId: "rel", key: "last-feature", title: "last feature", branchName: "feature/last-feature", status: "InProgress",
    createdAt: "2026-01-02T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
    stageRuns: [run()],
    signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" }, ...o,
  };
}

function release(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel", workspacePath: "D:\\apps\\calculator", title: "Release adding", version: "0.1.0", status: "InProgress",
    branchName: "release/adding", currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
    features: [feature()], stageRuns: [run()], signoffs: [], flowPosition: null, ...o,
  };
}

// The bug this file exists for.
//
// The user clicked "Check readiness for Developer" in the BA stage and the UI went mute. The
// checks actually ran and passed server-side; what went wrong was entirely in the composition:
//
//   - the gates passing sets readyToProceed, which makes FeatureView swap the whole primary panel
//     (conversation + stage log + cruise control) for the stage-complete card, and
//   - the only thing that would have approved the stage automatically lived in that panel.
//
// So the panel teardown destroyed the countdown on the very event the countdown was waiting for,
// and its result was set on an already-unmounted ChatStage. The user was left on a card that
// said "review this" with nothing moving and no reason given - which is what "stuck" looks like.
describe("FeatureView when a stage's checks have passed and a sign-off is pending", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest.useFakeTimers();
    mockApi.getPipelineAsync.mockResolvedValue(PIPELINE);
    mockApi.getStageMessagesAsync.mockResolvedValue([
      { id: "m1", role: "assistant", bodyText: "Requirements settled.", createdAt: "2026-01-02T00:00:00Z", parts: [], isPriming: false },
    ]);
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getSessionAsync.mockResolvedValue({ modelId: "a/one" } as never);
    mockApi.getReleaseAsync.mockResolvedValue(release());
    mockApi.signoffFeatureAsync.mockResolvedValue(release() as unknown as ReleaseDto);
  });

  afterEach(() => jest.useRealTimers());

  // One act per tick, so each 1-second state change actually re-renders and schedules the next
  // timer; one large advance never lets the effect re-arm, so the countdown simply never
  // finishes (the lesson CruiseControl.test.tsx:41 encodes). Also drives FeatureView's pollers,
  // which is where the next trap lives: getReleaseAsync must keep answering the SAME fixture
  // the test rendered with, or the first poll flattens autonomousEnabled and the countdown
  // vanishes mid-test.
  async function tickSeconds(seconds: number) {
    for (let i = 0; i < seconds; i++) {
      await act(async () => {
        jest.advanceTimersByTime(1000);
      });
    }
    await act(async () => undefined);
  }

  function renderView(rel: ReleaseDto) {
    mockApi.getReleaseAsync.mockResolvedValue(rel);
    return render(
      <FeatureView api={mockApi as unknown as BrokerApi} release={rel} featureId="f1" onReleaseUpdated={jest.fn()} />,
    );
  }

  it("shows the sign-off card instead of the conversation", async () => {
    renderView(release({ autonomousEnabled: true }));

    expect(await screen.findByTestId("release-stage-complete-card")).toBeInTheDocument();
    expect(screen.queryByTestId("release-chat-input")).not.toBeInTheDocument();
  });

  it("counts the approval down on the card, which outlives the panel it replaced", async () => {
    renderView(release({ autonomousEnabled: true }));

    await waitFor(() =>
      expect(screen.getByTestId("release-approve-countdown-btn")).toHaveTextContent(`Approving in ${SIGN_OFF_SECONDS} seconds`),
    );
  });

  it("approves on its own, so a passed check actually moves the user forward", async () => {
    renderView(release({ autonomousEnabled: true }));
    await waitFor(() => screen.getByTestId("release-approve-countdown-btn"));

    await tickSeconds(SIGN_OFF_SECONDS);

    await waitFor(() =>
      expect(mockApi.signoffFeatureAsync).toHaveBeenCalledWith("f1", "business-analyst", "user", "Approved via wizard"),
    );
  });

  it("approves exactly once - the cruise-control countdown must not also fire", async () => {
    // Two timers on one sign-off is a race, and the loser still fires against a stage the user
    // has already moved past. Exactly one of them owns the decision.
    renderView(release({ autonomousEnabled: true }));
    await waitFor(() => screen.getByTestId("release-approve-countdown-btn"));

    await tickSeconds(SIGN_OFF_SECONDS);

    await waitFor(() => expect(mockApi.signoffFeatureAsync).toHaveBeenCalledTimes(1));
  });

  it("waits for the whole window rather than approving the moment the checks passed", async () => {
    renderView(release({ autonomousEnabled: true }));
    await waitFor(() => screen.getByTestId("release-approve-countdown-btn"));

    await tickSeconds(SIGN_OFF_SECONDS - 1);

    expect(mockApi.signoffFeatureAsync).not.toHaveBeenCalled();
  });

  it("approves nothing on its own when autonomous mode is off", async () => {
    renderView(release({ autonomousEnabled: false }));

    expect(await screen.findByTestId("release-proceed-btn")).toBeInTheDocument();
    expect(screen.queryByTestId("release-approve-countdown-btn")).not.toBeInTheDocument();

    await tickSeconds(SIGN_OFF_SECONDS * 2);
    expect(mockApi.signoffFeatureAsync).not.toHaveBeenCalled();
  });
});
