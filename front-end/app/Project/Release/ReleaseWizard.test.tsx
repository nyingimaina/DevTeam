import React from "react";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ReleaseWizard from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval" },
  { name: "developer", userInputRequired: false, signoff: null },
  { name: "qa", userInputRequired: false, signoff: "release-approval" },
];

function makeRun(overrides: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr1",
    releaseId: "r1",
    stageName: "business-analyst",
    status: "Active",
    phase: "GuidedQA",
    questionCount: 0,
    attempt: 1,
    startedAt: "2026-01-01T00:00:00Z",
    gateChecks: [],
    findings: [],
    guidanceNotes: [],
    ...overrides,
  };
}

function makeRelease(overrides: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "00000000-0000-0000-0000-000000000001",
    workspacePath: "C:\\work\\proj",
    title: "Release login-form",
    version: "0.1.0",
    status: "InProgress",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    features: [{ id: "f1", releaseId: "r1", key: "login-form", title: "login-form", status: "InProgress", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" }],
    stageRuns: [],
    signoffs: [],
    flowPosition: { id: "fp1", releaseId: "r1", currentStageIndex: 0, currentStageName: "business-analyst" },
    ...overrides,
  };
}

async function openDetail(release: ReleaseDto) {
  const user = userEvent.setup();
  mockApi.listReleasesAsync.mockResolvedValue([release]);
  mockApi.getPipelineAsync.mockResolvedValue(PIPELINE);
  mockApi.getReleaseAsync.mockResolvedValue(release);
  render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);
  await waitFor(() => expect(screen.getByText("Release login-form")).toBeInTheDocument());
  await user.click(screen.getByTestId(`release-item-${release.id}`));
  await waitFor(() => expect(screen.getByTestId("pipeline-stepper")).toBeInTheDocument());
  return user;
}

describe("ReleaseWizard", () => {
  beforeEach(() => jest.clearAllMocks());

  it("loads and displays releases on mount", async () => {
    const releases = [makeRelease({ id: "r1" }), makeRelease({ id: "r2", title: "Release feat-b" })];
    mockApi.listReleasesAsync.mockResolvedValue(releases);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);

    await waitFor(() => {
      expect(screen.getByText("Release login-form")).toBeInTheDocument();
    });
    expect(screen.getByText("Release feat-b")).toBeInTheDocument();
    expect(mockApi.listReleasesAsync).toHaveBeenCalledTimes(1);
  });

  it("shows empty message when no releases exist", async () => {
    mockApi.listReleasesAsync.mockResolvedValue([]);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);

    await waitFor(() => {
      expect(screen.getByText("No releases yet.")).toBeInTheDocument();
    });
  });

  it("navigates to create view and creates a release", async () => {
    const user = userEvent.setup();
    mockApi.listReleasesAsync.mockResolvedValue([]);
    const created = makeRelease();
    mockApi.createReleaseAsync.mockResolvedValue(created);
    mockApi.getReleaseAsync.mockResolvedValue(created);
    mockApi.getPipelineAsync.mockResolvedValue(PIPELINE);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);

    await waitFor(() => {
      expect(screen.getByText("No releases yet.")).toBeInTheDocument();
    });

    await user.click(screen.getByText("New Release"));

    await user.type(screen.getByTestId("release-feature-key"), "login-form");
    await user.click(screen.getByTestId("release-create-btn"));

    await waitFor(() => {
      expect(mockApi.createReleaseAsync).toHaveBeenCalledWith("login-form", "C:\\work\\proj");
    });
    expect(screen.getByText("Release login-form")).toBeInTheDocument();
  });

  it("clicking a release navigates to the detail view with a pipeline stepper", async () => {
    const release = makeRelease({ id: "r1" });
    await openDetail(release);

    expect(screen.getByTestId("pipeline-step-business-analyst")).toHaveTextContent("1 business-analyst");
    expect(screen.getByTestId("pipeline-step-developer")).toHaveTextContent("2 developer");
    expect(screen.getByTestId("pipeline-step-qa")).toHaveTextContent("3 qa");
    expect(mockApi.getPipelineAsync).toHaveBeenCalledWith(release.id);
  });

  it("starts an interactive stage and shows the real chat panel with run gates button", async () => {
    const user = await openDetail(makeRelease());
    const started = makeRun({ id: "sr-live" });

    let startedStage = false;
    mockApi.getReleaseAsync.mockImplementation(async () => {
      if (startedStage) {
        return makeRelease({ stageRuns: [started] });
      }
      return makeRelease();
    });
    mockApi.startStageAsync.mockImplementation(async () => {
      startedStage = true;
      return started as never;
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);

    await user.click(screen.getByTestId("release-start-stage-btn"));

    await waitFor(() => {
      expect(screen.getByTestId("release-chat-input")).toBeInTheDocument();
    });
    expect(screen.getByTestId("release-send-btn")).toBeInTheDocument();
    expect(screen.getByTestId("release-run-gates-btn")).toBeInTheDocument();
    expect(mockApi.startStageAsync).toHaveBeenCalledTimes(1);
  });

  it("loads and renders the persisted conversation from the stage messages endpoint", async () => {
    const run = makeRun({ id: "sr-live", questionCount: 2 });
    const release = makeRelease({ stageRuns: [run] });
    mockApi.getStageMessagesAsync.mockResolvedValue([
      { id: "m1", role: "user", bodyText: "Add client-side validation to the login form.", createdAt: "2026-01-01T00:00:00Z", parts: [] },
      { id: "m2", role: "assistant", bodyText: "I added a validator. Say DONE when you want gates.", createdAt: "2026-01-01T00:00:02Z", parts: [] },
    ]);
    const user = await openDetail(release);

    await waitFor(() => {
      expect(screen.getByText("Add client-side validation to the login form.")).toBeInTheDocument();
    });
    expect(screen.getByText("I added a validator. Say DONE when you want gates.")).toBeInTheDocument();
    expect(mockApi.getStageMessagesAsync).toHaveBeenCalledWith(release.id, "sr-live");
  });

  it("sends a stage message and reloads the conversation", async () => {
    const run = makeRun({ id: "sr-live" });
    mockApi.getStageMessagesAsync
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        { id: "m1", role: "user", bodyText: "Start the analysis.", createdAt: "2026-01-01T00:00:00Z", parts: [] },
        { id: "m2", role: "assistant", bodyText: "Done.", createdAt: "2026-01-01T00:00:01Z", parts: [] },
      ]);
    mockApi.sendStageMessageAsync.mockResolvedValue({ response: "ok", inputTokens: 1, outputTokens: 1, totalTokens: 2 });
    const user = await openDetail(makeRelease({ stageRuns: [run] }));

    await user.type(screen.getByTestId("release-chat-input"), "Start the analysis.");
    await user.click(screen.getByTestId("release-send-btn"));

    await waitFor(() => {
      expect(mockApi.sendStageMessageAsync).toHaveBeenCalledWith("00000000-0000-0000-0000-000000000001", "Start the analysis.");
    });
    await waitFor(() => {
      expect(screen.getByText("Done.")).toBeInTheDocument();
    });
  });

  it("shows the autonomous stage log with gate results and a push-back panel when gates fail", async () => {
    const run = makeRun({
      id: "sr-dev",
      stageName: "developer",
      status: "BlockedGate",
      phase: "Gates",
      attempt: 1,
      gateChecks: [
        { id: "g1", stageRunId: "sr-dev", name: "verify_code", passed: false, evidenceText: "tests failed", completedAt: "2026-01-01T00:00:10Z" },
      ],
      findings: [
        { id: "f1", stageRunId: "sr-dev", target: "src/app/login.tsx", kind: "code", severity: "Blocker", summary: "Missing validation", status: "Open" },
      ],
      guidanceNotes: [{ id: "n1", stageRunId: "sr-dev", text: "Add validation next time.", addedBy: "business-analyst", createdAt: "2026-01-01T00:00:11Z" }],
    });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseId: "r1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    mockApi.runStageAsync.mockResolvedValue(release);
    const refreshed = makeRelease({
      flowPosition: { id: "fp1", releaseId: "r1", currentStageIndex: 2, currentStageName: "qa" },
      stageRuns: [run, makeRun({ id: "sr-qa", stageName: "qa" })],
    });
    mockApi.getReleaseAsync.mockResolvedValueOnce(release).mockResolvedValue(refreshed);
    mockApi.pushBackAsync.mockResolvedValue(refreshed);

    const user = await openDetail(release);

    const log = screen.getByTestId("release-stage-log");
    await waitFor(() => {
      expect(within(log).getByText(/gate verify_code/)).toBeInTheDocument();
    });
    expect(within(log).getByText(/Missing validation/)).toBeInTheDocument();
    expect(within(log).getByText(/Add validation next time/)).toBeInTheDocument();

    await user.selectOptions(screen.getByTestId("release-pushback-target"), "business-analyst");
    await user.type(screen.getByTestId("release-pushback-instructions"), "Rework the login form.");
    await waitFor(() => {
      expect(screen.getByTestId("release-pushback-instructions")).toHaveValue("Rework the login form.");
    });
    await user.click(screen.getByTestId("release-pushback-btn"));

    await waitFor(() => {
      expect(mockApi.pushBackAsync).toHaveBeenCalledWith(release.id, "business-analyst", "Rework the login form.");
    });
  });

  it("shows signoff button for a blocked release", async () => {
    const release = makeRelease({
      status: "Blocked",
      signoffs: [{ id: "s1", releaseId: "r1", stageName: "requirements-approval", required: true, approved: false }],
    });
    await openDetail(release);

    expect(screen.getByText("Signoff Required")).toBeInTheDocument();
    expect(screen.getByText(/Approve requirements-approval/)).toBeInTheDocument();
  });
});