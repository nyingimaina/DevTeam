import React from "react";
import { act, fireEvent, render, screen } from "@testing-library/react";
import "@testing-library/jest-dom";
import CruiseControl, { AUTO_ADVANCE_SECONDS } from "./CruiseControl";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

const pipeline = [
  { name: "business-analyst", userInputRequired: true },
  { name: "developer", userInputRequired: false },
  { name: "qa", userInputRequired: false },
  { name: "verification", userInputRequired: false },
] as PipelineStageDto[];

function run(stageName: string, status: string): ReleaseStageRunDto {
  return { id: "r", stageName, status, attempt: 1, phase: "", questionCount: 0, readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [] } as unknown as ReleaseStageRunDto;
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

// One act per tick, so each 1-second state change actually re-renders and schedules the next.
async function tickCountdown() {
  for (let i = 0; i < AUTO_ADVANCE_SECONDS; i++) {
    await act(async () => {
      jest.advanceTimersByTime(1000);
    });
  }
  // Flush the async action fired on the final tick.
  await act(async () => undefined);
}

function renderControl(current: ReleaseDto | undefined) {
  const refreshRelease = jest.fn();
  render(
    <CruiseControl
      featureId="f1"
      release={current}
      pipeline={pipeline}
      api={mockApi as unknown as BrokerApi}
      testIdPrefix="stage"
      refreshRelease={refreshRelease}
    />,
  );
  return { refreshRelease };
}

describe("CruiseControl", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    jest.useFakeTimers();
    mockApi.setReleaseAutonomousEnabledAsync.mockResolvedValue(release());
  });

  afterEach(() => jest.useRealTimers());

  it("renders nothing without a release to act on", () => {
    renderControl(undefined);

    expect(screen.queryByTestId("stage-cruise")).toBeNull();
  });

  it("is always visible and toggleable, even during the BA's own conversation", () => {
    // Regression: the toggle used to be hidden entirely until armed, specifically during the
    // interactive stage — so there was no way to pre-arm it before the conversation finished.
    renderControl(
      release({ stageRuns: [run("business-analyst", "Stale")], flowPosition: { id: "f", releaseFeatureId: "x", currentStageIndex: 0, currentStageName: "business-analyst" } }),
    );

    const toggle = screen.getByTestId("stage-cruise-toggle");
    expect(toggle).toBeInTheDocument();
    expect(toggle).not.toBeChecked();
  });

  it("reflects the release's persisted autonomousEnabled on mount", () => {
    renderControl(release({ autonomousEnabled: true, stageRuns: [run("developer", "Stale")] }));

    expect(screen.getByTestId("stage-cruise-toggle")).toBeChecked();
  });

  it("persists turning it on", () => {
    renderControl(release({ id: "r1", autonomousEnabled: false, stageRuns: [run("developer", "Stale")] }));

    fireEvent.click(screen.getByTestId("stage-cruise-toggle"));

    expect(mockApi.setReleaseAutonomousEnabledAsync).toHaveBeenCalledWith("r1", true);
    expect(screen.getByTestId("stage-cruise-toggle")).toBeChecked();
  });

  it("persists turning it off — a single click, not a separate stop action", () => {
    renderControl(release({ id: "r1", autonomousEnabled: true, stageRuns: [run("developer", "Stale")] }));

    fireEvent.click(screen.getByTestId("stage-cruise-toggle"));

    expect(mockApi.setReleaseAutonomousEnabledAsync).toHaveBeenCalledWith("r1", false);
    expect(screen.getByTestId("stage-cruise-toggle")).not.toBeChecked();
  });

  it("can be turned off while a stage is actively running, with no separate stop step", () => {
    renderControl(release({ id: "r1", autonomousEnabled: true, stageRuns: [run("developer", "Active")] }));

    fireEvent.click(screen.getByTestId("stage-cruise-toggle"));

    expect(mockApi.setReleaseAutonomousEnabledAsync).toHaveBeenCalledWith("r1", false);
  });

  it("runs the next autonomous stage after the countdown while on", async () => {
    // A "Stale" run (reconciler-marked orphaned session) is a real run-stage case that is NOT
    // AutoRunNotice's moment — unlike a truly fresh stage (no run at all), which CruiseControl
    // defers to AutoRunNotice entirely (see the "defers to AutoRunNotice" test below).
    mockApi.runStageAsync.mockResolvedValue(release({ stageRuns: [run("developer", "Active")] }));
    const { refreshRelease } = renderControl(release({ autonomousEnabled: true, stageRuns: [run("developer", "Stale")] }));

    expect(screen.getByTestId("stage-cruise-message")).toHaveTextContent(/starting the next step/i);
    await tickCountdown();

    expect(mockApi.runStageAsync).toHaveBeenCalledWith("f1");
    expect(refreshRelease).toHaveBeenCalled();
  });

  it("approves a stage that is only waiting for approval", async () => {
    const waiting = release({
      autonomousEnabled: true,
      stageRuns: [run("developer", "BlockedSignoff")],
      signoffs: [{ id: "s", releaseFeatureId: "x", stageName: "developer", required: true, approved: false }],
    });
    mockApi.signoffFeatureAsync.mockResolvedValue(release());
    renderControl(waiting);

    await tickCountdown();

    expect(mockApi.signoffFeatureAsync).toHaveBeenCalledWith("f1", "developer", "automatic", expect.any(String));
  });

  it("shows a plain status message when a stage needs a human, and stays on", async () => {
    renderControl(release({ autonomousEnabled: true, stageRuns: [run("developer", "BlockedGate")] }));

    expect(screen.getByTestId("stage-cruise-message")).toHaveTextContent(/attention/i);
    // Unlike before, a blocker no longer turns the toggle off on its own — the preference is
    // "keep going whenever possible," which resumes once the block clears.
    expect(screen.getByTestId("stage-cruise-toggle")).toBeChecked();
    expect(mockApi.setReleaseAutonomousEnabledAsync).not.toHaveBeenCalled();
  });

  it("defers to AutoRunNotice for a fresh non-interactive stage instead of racing it", async () => {
    // Regression: AutoRunNotice fires runStageAsync immediately (unconditionally) the moment
    // a fresh non-interactive stage mounts. If CruiseControl is already on at that same
    // moment (persisted-on-load), its own countdown must not also race toward the same call.
    renderControl(release({ autonomousEnabled: true, stageRuns: [] }));

    expect(screen.getByTestId("stage-cruise-toggle")).toBeChecked();
    expect(screen.queryByTestId("stage-cruise-message")).toBeNull();

    // Even after the countdown's own window would have elapsed, nothing acts here — this stays
    // AutoRunNotice's moment until a run actually exists.
    await tickCountdown();
    expect(mockApi.runStageAsync).not.toHaveBeenCalled();
  });
});
