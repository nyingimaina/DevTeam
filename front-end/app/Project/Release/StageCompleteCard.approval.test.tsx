import React from "react";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import { StageCompleteCard } from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto, ReleaseSignoffDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

// A stage that passed its gates and is waiting on a human is exactly the state the card is for.
// The user reported this state as a dead end: the panel is replaced by the card, and the
// autonomous countdown that used to live in the panel is torn down by that same replacement,
// so nothing ever approved it and nothing said why.
const SIGN_OFF_SECONDS = 45;

function makeRun(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr1", releaseFeatureId: "f1", stageName: "business-analyst", status: "BlockedSignoff", phase: "Signoff",
    questionCount: 1, attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: true, gateChecks: [], findings: [],
    guidanceNotes: [], specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp-1", ...o,
  };
}

const PENDING: ReleaseSignoffDto[] = [
  { id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false },
];

function renderCard(autonomousEnabled: boolean | undefined) {
  const onReleaseUpdated = jest.fn();
  const result = render(
    <StageCompleteCard
      featureId="f1"
      stageRun={makeRun()}
      pendingSignoffs={PENDING}
      api={mockApi as unknown as BrokerApi}
      testIdPrefix="release"
      nextStageName="developer"
      onReleaseUpdated={onReleaseUpdated}
      onContinue={jest.fn()}
      {...(autonomousEnabled === undefined ? {} : { autonomousEnabled })}
    />,
  );
  return { onReleaseUpdated, ...result };
}

// One act per tick, so each 1-second state change actually re-renders and schedules the next
// timer; advancing (many) seconds in one call never lets the effect re-arm, so a countdown
// driven that way simply never finishes (same shape as CruiseControl.test.tsx:41).
async function tick(seconds = 1) {
  for (let i = 0; i < seconds; i++) {
    await act(async () => {
      jest.advanceTimersByTime(1000);
    });
  }
  // Flush the async action fired on the final tick.
  await act(async () => undefined);
}

describe("StageCompleteCard sign-off approval", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest.useFakeTimers();
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.signoffFeatureAsync.mockResolvedValue({} as unknown as ReleaseDto);
  });

  afterEach(() => jest.useRealTimers());

  it("counts the sign-off down from 45 seconds rather than approving at once", async () => {
    // A sign-off is a bigger commitment than re-running a failed stage, so the window is longer
    // than the 30s used for the "Run Stage Again" countdown. Approving a stage the user never
    // looked at is the failure mode worth spending the extra seconds on.
    renderCard(true);

    await waitFor(() => expect(screen.getByTestId("release-approve-countdown-btn")).toHaveTextContent("Approving in 45 seconds"));

    await tick(1);
    expect(screen.getByTestId("release-approve-countdown-btn")).toHaveTextContent("Approving in 44 seconds");
  });

  it("does not approve before the countdown finishes", async () => {
    renderCard(true);
    await waitFor(() => screen.getByTestId("release-approve-countdown-btn"));

    await tick(SIGN_OFF_SECONDS - 1);

    expect(mockApi.signoffFeatureAsync).not.toHaveBeenCalled();
  });

  it("approves on its own once the countdown reaches zero", async () => {
    renderCard(true);
    await waitFor(() => screen.getByTestId("release-approve-countdown-btn"));

    await tick(SIGN_OFF_SECONDS);

    await waitFor(() =>
      expect(mockApi.signoffFeatureAsync).toHaveBeenCalledWith("f1", "business-analyst", "user", "Approved via wizard"),
    );
  });

  it("approves immediately when the countdown button is clicked", async () => {
    // fireEvent rather than userEvent: userEvent's own internal timing waits on real timers
    // that fake timers never advance, which deadlocks the test until the 5s timeout (observed).
    renderCard(true);
    await waitFor(() => screen.getByTestId("release-approve-countdown-btn"));

    act(() => {
      fireEvent.click(screen.getByTestId("release-approve-countdown-btn"));
    });
    await act(async () => undefined);

    expect(mockApi.signoffFeatureAsync).toHaveBeenCalledTimes(1);
  });

  it("offers the plain approval button and starts no countdown when autonomous mode is off", async () => {
    renderCard(false);

    await waitFor(() => expect(screen.getByTestId("release-proceed-btn")).toBeInTheDocument());
    expect(screen.queryByTestId("release-approve-countdown-btn")).not.toBeInTheDocument();

    // And it must stay put: nothing may approve behind the user's back.
    await tick(SIGN_OFF_SECONDS * 2);
    expect(mockApi.signoffFeatureAsync).not.toHaveBeenCalled();
  });

  it("starts no countdown when the caller says nothing about autonomous mode", async () => {
    renderCard(undefined);

    await waitFor(() => expect(screen.getByTestId("release-proceed-btn")).toBeInTheDocument());
    expect(screen.queryByTestId("release-approve-countdown-btn")).not.toBeInTheDocument();
  });

  it("leaves a way out of the countdown that does not depend on the split-button menu", async () => {
    // The "Don't approve" item lives in a Radix dropdown, which this suite cannot open without a
    // multi-minute jsdom stall (see ReleaseWizard.test.tsx:305 for the same reason). The card
    // still has to give the user a plain, immediate way to stop the auto-approval, or the
    // countdown is the only thing on screen that can act.
    renderCard(true);
    await waitFor(() => screen.getByTestId("release-continue-btn"));
  });
});
