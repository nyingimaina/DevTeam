import React from "react";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import { StageScreen } from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: [], steps: ["agent:business-analyst"] },
];

// What the broker says when the checks themselves could not be run. The user has to be able to
// read this after the fact, so it goes in the test the way it would appear to them.
const CHECK_FAILED =
  'The checks could not be run: the agent process stopped unexpectedly (reference: 7c1de0aa). ' +
  "Nothing has been lost - your work is still there.";

function makeRun(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr1", releaseFeatureId: "f1", stageName: "business-analyst", status: "Active", phase: "GuidedQA",
    questionCount: 1, attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false, gateChecks: [],
    findings: [], guidanceNotes: [], specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp-1", ...o,
  };
}

function makeRelease(run?: ReleaseStageRunDto, o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel1", workspacePath: "C:\\work\\proj", title: "Release", version: "0.1.0", status: "InProgress", branchName: "release/x",
    currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
    features: [], stageRuns: run ? [run] : [], signoffs: [],
    flowPosition: { id: "fp", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" },
    ...o,
  };
}

function renderStage(release: ReleaseDto, run: ReleaseStageRunDto) {
  return render(
    <StageScreen
      release={release}
      featureId="f1"
      api={mockApi as unknown as BrokerApi}
      pipeline={PIPELINE}
      role={PIPELINE[0]}
      run={run}
      testIdPrefix="release"
      refreshRelease={async () => undefined}
    />,
  );
}

async function checkAndFail(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByTestId("release-move-on-btn"));
}

describe("StageScreen when the readiness check itself fails", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getSessionAsync.mockResolvedValue({ modelId: "a/one" } as never);
    mockApi.runStageGatesWithRepairAsync.mockRejectedValue(new Error(CHECK_FAILED));
  });

  it("says the checks failed and why, instead of leaving the button looking stuck", async () => {
    renderStage(makeRelease(), makeRun());

    await checkAndFail(userEvent.setup());

    expect(await screen.findByTestId("release-gates-error")).toHaveTextContent(/checks could not be run/i);
  });

  it("still says so after the transcript poller has ticked", async () => {
    // The bug. Reading the transcript says nothing about whether the checks ran - and that read
    // always succeeds, even when the check request is what failed. Sharing the transcript's error
    // state meant a successful poll wiped the reason within 2.5s, so the button sprang back with
    // no explanation at all. This is the same defect already fixed for a refused send, which is
    // why that fix has its own dedicated state and test (StageScreen.sendFailure.test.tsx:84).
    renderStage(makeRelease(), makeRun());

    await checkAndFail(userEvent.setup());
    await screen.findByTestId("release-gates-error");

    await new Promise((r) => setTimeout(r, 3200)); // one poll tick plus slack

    expect(screen.getByTestId("release-gates-error")).toHaveTextContent(/checks could not be run/i);
  });

  it("re-enables the button so the checks can simply be tried again", async () => {
    renderStage(makeRelease(), makeRun());
    const user = userEvent.setup();

    await checkAndFail(user);
    await screen.findByTestId("release-gates-error");

    // A dead end here is the same failure as a silent one: the work is still on disk, so the fix
    // has to be another click, not a page reload. The design system keeps a clicked submit
    // button locked for ~2s afterwards, so the wait has to outlast that lock.
    await waitFor(() => expect(screen.getByTestId("release-move-on-btn")).toBeEnabled(), { timeout: 3000 });
  });

  it("clears the previous failure when a new check is started", async () => {
    renderStage(makeRelease(), makeRun());
    const user = userEvent.setup();

    await checkAndFail(user);
    await screen.findByTestId("release-gates-error");

    // Same as above: the design system's post-click lock has to expire before a second check can
    // be fired, and a check that never completes must take the old complaint down with it,
    // otherwise the stale text reads as "this just failed again" while this one is in flight.
    await new Promise((r) => setTimeout(r, 2200));
    await waitFor(() => expect(screen.getByTestId("release-move-on-btn")).toBeEnabled(), { timeout: 3000 });

    mockApi.runStageGatesWithRepairAsync.mockReturnValue(new Promise(() => undefined));
    await act(async () => {
      await user.click(screen.getByTestId("release-move-on-btn"));
    });

    await waitFor(() => expect(screen.queryByTestId("release-gates-error")).not.toBeInTheDocument());
  });
});
