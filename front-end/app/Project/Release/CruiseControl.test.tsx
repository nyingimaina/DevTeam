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
  });

  afterEach(() => jest.useRealTimers());

  it("offers nothing while the conversation stage is still in front of the user", () => {
    renderControl(
      release({ flowPosition: { id: "f", releaseFeatureId: "x", currentStageIndex: 0, currentStageName: "business-analyst" } }),
    );

    expect(screen.queryByTestId("stage-cruise")).toBeNull();
  });

  it("runs the next autonomous stage after the countdown", async () => {
    mockApi.runStageAsync.mockResolvedValue(release({ stageRuns: [run("developer", "Active")] }));
    const { refreshRelease } = renderControl(release());

    fireEvent.click(screen.getByTestId("stage-cruise-start"));
    expect(screen.getByTestId("stage-cruise-countdown")).toBeInTheDocument();

    await tickCountdown();

    expect(mockApi.runStageAsync).toHaveBeenCalledWith("f1");
    expect(refreshRelease).toHaveBeenCalled();
  });

  it("approves a stage that is only waiting for approval", async () => {
    const waiting = release({
      stageRuns: [run("developer", "BlockedSignoff")],
      signoffs: [{ id: "s", releaseFeatureId: "x", stageName: "developer", required: true, approved: false }],
    });
    mockApi.signoffFeatureAsync.mockResolvedValue(release());
    renderControl(waiting);

    fireEvent.click(screen.getByTestId("stage-cruise-start"));
    await tickCountdown();

    expect(mockApi.signoffFeatureAsync).toHaveBeenCalledWith("f1", "developer", "automatic", expect.any(String));
  });

  it("stops with a plain message when a stage needs a human", async () => {
    renderControl(release({ stageRuns: [run("developer", "BlockedGate")] }));

    fireEvent.click(screen.getByTestId("stage-cruise-start"));

    expect(screen.getByTestId("stage-cruise-message")).toHaveTextContent(/attention/i);
    expect(screen.queryByTestId("stage-cruise-countdown")).toBeNull();
  });
});
