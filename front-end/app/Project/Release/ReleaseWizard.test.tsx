import React from "react";
import { render, screen, waitFor, within, act, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ReleaseWizard from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: ["devteam/features/<F>/specs.feature", "devteam/features/<F>/handoff.md"], steps: ["scaffold_specs", "context_bundle", "agent:business-analyst", "gherkin_validator", "render_handoff"] },
  { name: "developer", userInputRequired: false, signoff: null, expectedArtifacts: ["devteam/features/<F>/code/"], steps: ["context_bundle", "agent:developer", "verify_code", "code_hygiene", "slice_guard", "render_pr"] },
  { name: "qa", userInputRequired: false, signoff: "release-approval", expectedArtifacts: ["devteam/features/<F>/coverage.md"], steps: ["context_bundle", "agent:qa", "verify_code", "coverage_matrix", "render_handoff"] },
];

function makeRun(overrides: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr1",
    releaseFeatureId: "f1",
    stageName: "business-analyst",
    status: "Active",
    phase: "GuidedQA",
    questionCount: 0,
    attempt: 1,
    startedAt: "2026-01-01T00:00:00Z",
    readyToProceed: false,
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
    branchName: "release/login-form",
    currentFeatureId: "f1",
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    features: [{ id: "f1", releaseId: "r1", key: "login-form", title: "login-form", branchName: "feature/login-form", status: "InProgress", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" }],
    stageRuns: [],
    signoffs: [],
    flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" },
    ...overrides,
  };
}

// userEvent.click hangs indefinitely on ZestButton's split-button dropdown trigger in this
// jsdom environment (confirmed via isolated repro — Radix's own pointer-capture interaction
// with userEvent's pointer simulation never resolves, even with PointerEvent/hasPointerCapture
// polyfilled in jest.setup.js). A raw fireEvent pointer/mouse sequence opens and drives the
// same Radix menu reliably and fast, so use this instead of userEvent.click for that control.
function clickViaPointerSequence(el: Element) {
  fireEvent.pointerDown(el, { pointerId: 1, button: 0, pointerType: "mouse", isPrimary: true });
  fireEvent.mouseDown(el, { button: 0 });
  fireEvent.pointerUp(el, { pointerId: 1, button: 0, pointerType: "mouse", isPrimary: true });
  fireEvent.mouseUp(el, { button: 0 });
  fireEvent.click(el);
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
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
  });

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

  it("scopes the release list to the current project's workspace", async () => {
    mockApi.listReleasesAsync.mockResolvedValue([]);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);

    await waitFor(() => {
      expect(mockApi.listReleasesAsync).toHaveBeenCalledWith("C:\\work\\proj");
    });
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

    expect(screen.getByTestId("pipeline-step-business-analyst")).toHaveTextContent("1 Business Analyst");
    expect(screen.getByTestId("pipeline-step-developer")).toHaveTextContent("2 Developer");
    expect(screen.getByTestId("pipeline-step-qa")).toHaveTextContent("3 QA");
    expect(screen.getByText("Advanced details")).toBeInTheDocument();
    expect(mockApi.getPipelineAsync).toHaveBeenCalledWith(release.currentFeatureId);
  });

  it("starts an interactive stage and shows the real chat panel (gates run automatically)", async () => {
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
    expect(screen.queryByTestId("release-run-gates-btn")).not.toBeInTheDocument();
    expect(screen.getByText(/Answer the agent's prompts one at a time/)).toBeInTheDocument();
    expect(mockApi.startStageAsync).toHaveBeenCalledTimes(1);
  });

  it("still requires an explicit manual click to start a fresh interactive stage", async () => {
    await openDetail(makeRelease());

    const btn = await screen.findByTestId("release-start-stage-btn");
    expect(btn).toHaveTextContent("Start Conversation");
    expect(mockApi.startStageAsync).not.toHaveBeenCalled();
  });

  it("auto-runs a fresh autonomous stage immediately, with no click required", async () => {
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [],
    });
    const started = makeRun({ id: "sr-dev", stageName: "developer" });
    mockApi.runStageAsync.mockResolvedValue(makeRelease({ stageRuns: [started] }));
    mockApi.getReleaseAsync.mockResolvedValue(makeRelease({ stageRuns: [started] }));
    await openDetail(release);

    expect(await screen.findByTestId("release-auto-run-notice")).toBeInTheDocument();
    expect(screen.queryByTestId("release-start-stage-btn")).not.toBeInTheDocument();
    await waitFor(() => expect(mockApi.runStageAsync).toHaveBeenCalledWith(release.currentFeatureId));
  });

  it("auto-runs again with no click after landing on an already-Complete autonomous stage via push-back", async () => {
    // A push-back moves the flow position back to an earlier stage without touching that
    // stage's own (already Complete) run row — the frontend has to recognize "current stage,
    // but its latest run is Complete" as "needs a fresh attempt," not "already done."
    const staleRun = makeRun({ id: "sr-dev-1", stageName: "developer", status: "Complete", attempt: 1 });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [staleRun],
    });
    const freshRun = makeRun({ id: "sr-dev-2", stageName: "developer", attempt: 2 });
    mockApi.runStageAsync.mockResolvedValue(makeRelease({ stageRuns: [staleRun, freshRun] }));
    mockApi.getReleaseAsync.mockResolvedValue(makeRelease({ stageRuns: [staleRun, freshRun] }));
    await openDetail(release);

    expect(await screen.findByTestId("release-auto-run-notice")).toBeInTheDocument();
    await waitFor(() => expect(mockApi.runStageAsync).toHaveBeenCalledWith(release.currentFeatureId));
  });

  it("shows a manual Start Conversation again after landing on an already-Complete interactive stage via push-back", async () => {
    const staleRun = makeRun({ id: "sr-ba-1", stageName: "business-analyst", status: "Complete" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" },
      stageRuns: [staleRun],
    });
    await openDetail(release);

    const btn = await screen.findByTestId("release-start-stage-btn");
    expect(btn).toHaveTextContent("Start Conversation");
    expect(mockApi.startStageAsync).not.toHaveBeenCalled();
  });

  it("shows a countdown/cancel split button for Run Stage Again on a failed autonomous stage, and auto-retries when it reaches zero", async () => {
    jest.useFakeTimers({ advanceTimers: true });
    try {
      const run = makeRun({ id: "sr-dev", stageName: "developer", status: "BlockedGate" });
      const release = makeRelease({
        flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
        stageRuns: [run],
      });
      mockApi.runStageAsync.mockResolvedValue(release);
      await openDetail(release);

      const btn = await screen.findByTestId("release-run-stage-btn");
      expect(btn).toHaveTextContent("Running again in 30 seconds. Click To Run Now.");
      expect(screen.getByRole("button", { name: "More run options" })).toBeInTheDocument();

      for (let i = 0; i < 30; i++) {
        await act(async () => {
          jest.advanceTimersByTime(1000);
        });
      }
      await waitFor(() => expect(mockApi.runStageAsync).toHaveBeenCalledWith(release.currentFeatureId));
    } finally {
      jest.useRealTimers();
    }
  });

  it("retries immediately when the Run Stage Again countdown button itself is clicked", async () => {
    const run = makeRun({ id: "sr-dev", stageName: "developer", status: "BlockedGate" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    mockApi.runStageAsync.mockResolvedValue(release);
    const user = await openDetail(release);

    const btn = await screen.findByTestId("release-run-stage-btn");
    await user.click(btn);

    await waitFor(() => expect(mockApi.runStageAsync).toHaveBeenCalledWith(release.currentFeatureId));
  });

  it("cancels the Run Stage Again auto-retry when Don't Run is chosen, without ever calling runStageAsync", async () => {
    const run = makeRun({ id: "sr-dev", stageName: "developer", status: "BlockedGate" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    await openDetail(release);

    await screen.findByTestId("release-run-stage-btn");
    clickViaPointerSequence(screen.getByRole("button", { name: "More run options" }));
    const item = await screen.findByRole("menuitem", { name: "Don't Run" });
    clickViaPointerSequence(item);

    const btn = await screen.findByTestId("release-run-stage-btn");
    expect(btn).toHaveTextContent("Run Stage Again");
    expect(btn).not.toHaveTextContent(/Running again in/);
    expect(mockApi.runStageAsync).not.toHaveBeenCalled();
    // Reopening the same Radix dropdown a second time within one test hangs indefinitely in
    // this jsdom environment (confirmed via isolated repro, independent of any state change
    // in the app itself) — so this only verifies the one open/select cycle, not that the item
    // becomes disabled on reopen; disabled-item styling is a CSS-only concern regardless.
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
    expect(mockApi.getStageMessagesAsync).toHaveBeenCalledWith(release.currentFeatureId, "sr-live");
    expect(screen.getByText("Stage history")).toBeInTheDocument();
    expect(screen.getByText("Business Analyst")).toBeInTheDocument();
  });

  it("loads chat history on mount for a run that is not actively polling but is still the working run (BlockedSignoff)", async () => {
    // "Complete" is covered separately (needsFreshStart) — a current stage's own run only
    // reaches Complete via push-back, which now shows fresh-start controls instead of history.
    const run = makeRun({ id: "sr-live", status: "BlockedSignoff" });
    mockApi.getStageMessagesAsync.mockResolvedValue([
      { id: "m1", role: "user", bodyText: "Historical question.", createdAt: "2026-01-01T00:00:00Z", parts: [] },
      { id: "m2", role: "assistant", bodyText: "Historical answer.", createdAt: "2026-01-01T00:00:01Z", parts: [] },
    ]);
    await openDetail(makeRelease({ stageRuns: [run] }));

    await waitFor(() => {
      expect(screen.getByText("Historical answer.")).toBeInTheDocument();
    });
    expect(mockApi.getStageMessagesAsync).toHaveBeenCalledWith("f1", "sr-live");
  });

  it("does not force-scroll to bottom once the reader has scrolled up to review earlier output", async () => {
    const scrollIntoViewSpy = jest.fn();
    (HTMLElement.prototype as unknown as { scrollIntoView: () => void }).scrollIntoView = scrollIntoViewSpy;
    jest.useFakeTimers({ advanceTimers: true });
    try {
      const run = makeRun({ id: "sr-live" });
      mockApi.getStageMessagesAsync
        .mockResolvedValueOnce([
          { id: "m1", role: "assistant", bodyText: "First message.", createdAt: "2026-01-01T00:00:00Z", parts: [] },
        ])
        .mockResolvedValueOnce([
          { id: "m1", role: "assistant", bodyText: "First message.", createdAt: "2026-01-01T00:00:00Z", parts: [] },
          { id: "m2", role: "assistant", bodyText: "Second message.", createdAt: "2026-01-01T00:00:01Z", parts: [] },
        ]);
      await openDetail(makeRelease({ stageRuns: [run] }));

      const container = await screen.findByTestId("release-chat-messages");
      scrollIntoViewSpy.mockClear();

      Object.defineProperty(container, "scrollHeight", { value: 1000, configurable: true });
      Object.defineProperty(container, "clientHeight", { value: 200, configurable: true });
      Object.defineProperty(container, "scrollTop", { value: 0, configurable: true });
      fireEvent.scroll(container);

      await act(async () => {
        jest.advanceTimersByTime(2500);
      });
      await waitFor(() => expect(screen.getByText("Second message.")).toBeInTheDocument());

      expect(scrollIntoViewSpy).not.toHaveBeenCalled();
    } finally {
      jest.useRealTimers();
    }
  });

  it("keeps polling chat history while the stage run is GatesRunning", async () => {
    jest.useFakeTimers({ advanceTimers: true });
    try {
      const run = makeRun({ id: "sr-live", status: "GatesRunning" });
      mockApi.getStageMessagesAsync.mockResolvedValue([]);
      await openDetail(makeRelease({ stageRuns: [run] }));

      await waitFor(() => expect(mockApi.getStageMessagesAsync).toHaveBeenCalledTimes(1));

      await act(async () => {
        jest.advanceTimersByTime(2500);
      });

      await waitFor(() => expect(mockApi.getStageMessagesAsync).toHaveBeenCalledTimes(2));
    } finally {
      jest.useRealTimers();
    }
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
      expect(mockApi.sendStageMessageAsync).toHaveBeenCalledWith("f1", "Start the analysis.");
    });
    await waitFor(() => {
      expect(screen.getByText("Done.")).toBeInTheDocument();
    });
  });

  it("auto-runs gates when the agent's latest message has a standalone DONE line", async () => {
    const release = makeRelease({ stageRuns: [makeRun({ id: "sr-live" })] });
    mockApi.getStageMessagesAsync
      .mockResolvedValueOnce([])
      .mockResolvedValueOnce([
        { id: "m1", role: "user", bodyText: "Start the analysis.", createdAt: "2026-01-01T00:00:00Z", parts: [] },
        { id: "m2", role: "assistant", bodyText: "Requirements settled.\nDONE\n\n### Notes", createdAt: "2026-01-01T00:00:01Z", parts: [] },
      ]);
    mockApi.sendStageMessageAsync.mockResolvedValue({ response: "ok", inputTokens: 1, outputTokens: 1, totalTokens: 2 });
    const user = await openDetail(release);

    await user.type(screen.getByTestId("release-chat-input"), "Start the analysis.");
    await user.click(screen.getByTestId("release-send-btn"));

    await waitFor(() => {
      expect(mockApi.runStageGatesAsync).toHaveBeenCalledWith(release.currentFeatureId);
    });
  });

  it("always offers a manual 'move to next stage' control, not just after a DONE line", async () => {
    const release = makeRelease({ stageRuns: [makeRun({ id: "sr-live", readyToProceed: false })] });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.runStageGatesAsync.mockResolvedValue(release);
    const user = await openDetail(release);

    const moveOnBtn = await screen.findByTestId("release-move-on-btn");
    expect(moveOnBtn).toHaveTextContent("Check readiness for Developer");

    await user.click(moveOnBtn);

    await waitFor(() => {
      expect(mockApi.runStageGatesAsync).toHaveBeenCalledWith(release.currentFeatureId);
    });
    // Regression: this control only re-checks gate readiness — it must never itself
    // approve a signoff or advance the pipeline (that's the Approve/Proceed button's job).
    expect(mockApi.signoffFeatureAsync).not.toHaveBeenCalled();
  });

  it("hides the manual move-on control once the stage is already ready to proceed", async () => {
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [makeRun({ id: "sr-live", status: "BlockedSignoff", readyToProceed: true })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    await openDetail(release);

    await screen.findByTestId("release-stage-complete-card");
    expect(screen.queryByTestId("release-move-on-btn")).not.toBeInTheDocument();
  });

  it("clicking Continue on the stage-complete card dismisses it without sending a message", async () => {
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [makeRun({ id: "sr-live", status: "BlockedSignoff", readyToProceed: true })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    const user = await openDetail(release);

    await screen.findByTestId("release-stage-complete-card");
    await user.click(screen.getByTestId("release-continue-btn"));

    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    expect(mockApi.signoffFeatureAsync).not.toHaveBeenCalled();
    expect(screen.getByTestId("release-chat-input")).toBeEnabled();
  });

  it("replaces the chat panel with the stage-complete card while ready to proceed, and restores it after Continue", async () => {
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [makeRun({ id: "sr-live", status: "BlockedSignoff", readyToProceed: true })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.getReleaseAsync.mockResolvedValue(release);
    const user = await openDetail(release);

    await screen.findByTestId("release-stage-complete-card");
    expect(screen.queryByTestId("release-chat-input")).not.toBeInTheDocument();

    await user.click(screen.getByTestId("release-continue-btn"));

    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    expect(screen.getByTestId("release-chat-input")).toBeInTheDocument();
    expect(screen.getByTestId("release-approve-now-btn")).toBeInTheDocument();
  });

  it("clicking Refresh re-surfaces a dismissed stage-complete card when the stage is still ready to proceed", async () => {
    // Regression: dismissing via Continue only clears until the stage stops being ready — but for
    // a stage just sitting in BlockedSignoff, nothing else flips readyToProceed back to false, so a
    // dismissed card previously stayed hidden forever with no UI path left to actually proceed.
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [makeRun({ id: "sr-live", status: "BlockedSignoff", readyToProceed: true })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.getReleaseAsync.mockResolvedValue(release);
    const user = await openDetail(release);

    await screen.findByTestId("release-stage-complete-card");
    await user.click(screen.getByTestId("release-continue-btn"));
    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Refresh" }));

    expect(await screen.findByTestId("release-stage-complete-card")).toBeInTheDocument();
  });

  it("keeps the approve control reachable after dismissing the card, once background sync ticks — no Refresh click needed", async () => {
    jest.useFakeTimers({ advanceTimers: true });
    try {
      const release = makeRelease({
        status: "BlockedSignoff",
        stageRuns: [makeRun({ id: "sr-live", status: "BlockedSignoff", readyToProceed: true })],
        signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
      });
      mockApi.getStageMessagesAsync.mockResolvedValue([]);
      mockApi.getReleaseAsync.mockResolvedValue(release);
      const user = await openDetail(release);

      await screen.findByTestId("release-stage-complete-card");
      await user.click(screen.getByTestId("release-continue-btn"));
      expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();

      // No Refresh click — just the background poller ticking on its own.
      await act(async () => {
        jest.advanceTimersByTime(3000);
      });

      const approveBtn = await screen.findByTestId("release-approve-now-btn");
      expect(approveBtn).toHaveTextContent("Approve & move to Developer");

      mockApi.signoffFeatureAsync.mockResolvedValue(
        makeRelease({
          stageRuns: [makeRun({ stageName: "developer", status: "Active" })],
          signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: true }],
          flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
        }),
      );
      await user.click(approveBtn);

      await waitFor(() => {
        expect(mockApi.signoffFeatureAsync).toHaveBeenCalledWith(release.currentFeatureId, "business-analyst", "user", "Approved via wizard");
      });
    } finally {
      jest.useRealTimers();
    }
  });

  it("recovers automatically once the backend comes back, with no user action (self-healing across a simulated outage)", async () => {
    jest.useFakeTimers({ advanceTimers: true });
    try {
      const initial = makeRelease({ stageRuns: [makeRun({ id: "sr-live", status: "Active", readyToProceed: false })] });
      mockApi.getStageMessagesAsync.mockResolvedValue([]);
      await openDetail(initial);

      let failuresLeft = 3;
      const recovered = makeRelease({
        status: "BlockedSignoff",
        stageRuns: [makeRun({ id: "sr-live", status: "BlockedSignoff", readyToProceed: true })],
        signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
      });
      mockApi.getReleaseAsync.mockImplementation(async () => {
        if (failuresLeft > 0) {
          failuresLeft--;
          throw new Error("backend down");
        }
        return recovered;
      });

      for (let i = 0; i < 6; i++) {
        await act(async () => {
          jest.advanceTimersByTime(3000);
        });
      }

      expect(await screen.findByTestId("release-stage-complete-card")).toBeInTheDocument();
    } finally {
      jest.useRealTimers();
    }
  });

  it("shows a thinking indicator while a stage message is in flight", async () => {
    const run = makeRun({ id: "sr-live" });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    let resolveSend!: (value: { response: string; inputTokens: number; outputTokens: number; totalTokens: number }) => void;
    mockApi.sendStageMessageAsync.mockImplementation(
      () => new Promise((res) => { resolveSend = res; }),
    );
    const user = await openDetail(makeRelease({ stageRuns: [run] }));

    await user.type(screen.getByTestId("release-chat-input"), "Start the analysis.");
    await user.click(screen.getByTestId("release-send-btn"));

    await waitFor(() => {
      expect(screen.getByTestId("release-thinking").textContent).toMatch(/thinking/i);
    });

    await act(async () => {
      resolveSend({ response: "ok", inputTokens: 1, outputTokens: 1, totalTokens: 2 });
    });
    await waitFor(() => {
      expect(screen.queryByTestId("release-thinking")).not.toBeInTheDocument();
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
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    mockApi.runStageAsync.mockResolvedValue(release);
    const refreshed = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 2, currentStageName: "qa" },
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

    // The novice shouldn't have to transcribe the raw failure output themselves —
    // instructions are already drafted from the failed gate/finding evidence, with
    // a clear banner guiding them to review it and push back.
    expect(screen.getByText(/We've drafted rework instructions/i)).toBeInTheDocument();
    const instructionsField = screen.getByTestId("release-pushback-instructions") as HTMLTextAreaElement;
    await waitFor(() => {
      expect(instructionsField.value).toContain("tests failed");
    });
    expect(instructionsField.value).toContain("Missing validation");

    await user.selectOptions(screen.getByTestId("release-pushback-target"), "business-analyst");
    await user.clear(instructionsField);
    await user.type(instructionsField, "Rework the login form.");
    await waitFor(() => {
      expect(screen.getByTestId("release-pushback-instructions")).toHaveValue("Rework the login form.");
    });
    await user.click(screen.getByTestId("release-pushback-btn"));

    await waitFor(() => {
      expect(mockApi.pushBackAsync).toHaveBeenCalledWith(release.currentFeatureId, "business-analyst", "Rework the login form.");
    });
  });

  it("shows a live running status with elapsed time when the active turn matches this stage's session", async () => {
    const run = makeRun({ id: "sr-dev", stageName: "developer", status: "Active", phase: "Producing", acpSessionId: "acp-dev-1" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    mockApi.getCurrentTurnAsync.mockResolvedValue({
      sessionId: "s1",
      acpSessionId: "acp-dev-1",
      preview: "implement the calculator",
      startedAt: new Date(Date.now() - 90_000).toISOString(),
    });
    await openDetail(release);

    const log = screen.getByTestId("release-stage-log");
    await waitFor(() => {
      expect(within(log).getByText(/running/i)).toBeInTheDocument();
    });
    expect(within(log).getByText(/1m30s/)).toBeInTheDocument();
  });

  it("shows a waiting status when the active turn belongs to a different session", async () => {
    const run = makeRun({ id: "sr-dev", stageName: "developer", status: "Active", phase: "Producing", acpSessionId: "acp-dev-1" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    mockApi.getCurrentTurnAsync.mockResolvedValue({
      sessionId: "s-other",
      acpSessionId: "acp-other-release",
      preview: "working on a different release",
      startedAt: new Date().toISOString(),
    });
    await openDetail(release);

    const log = screen.getByTestId("release-stage-log");
    await waitFor(() => {
      expect(within(log).getByText(/waiting/i)).toBeInTheDocument();
    });
    expect(within(log).getByText(/broker is busy/i)).toBeInTheDocument();
  });

  it("shows a stall hint when the stage is Active but no turn is actually running", async () => {
    const run = makeRun({ id: "sr-dev", stageName: "developer", status: "Active", phase: "Producing", acpSessionId: "acp-dev-1" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    await openDetail(release);

    const log = screen.getByTestId("release-stage-log");
    await waitFor(() => {
      expect(within(log).getByText(/may need a retry/i)).toBeInTheDocument();
    });
  });

  it("shows a step checklist that ticks off completed steps and spins on the current one", async () => {
    const run = makeRun({
      id: "sr-dev",
      stageName: "developer",
      status: "Active",
      gateChecks: [
        { id: "g1", stageRunId: "sr-dev", name: "context_bundle", passed: true, completedAt: "2026-01-01T00:00:01Z" },
      ],
    });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    await openDetail(release);

    const checklist = await screen.findByTestId("release-step-checklist");
    expect(within(checklist).getByTestId("release-step-context_bundle")).toHaveAttribute("data-status", "done");
    expect(within(checklist).getByTestId("release-step-agent:developer")).toHaveAttribute("data-status", "current");
    expect(within(checklist).getByTestId("release-step-code_hygiene")).toHaveAttribute("data-status", "pending");
  });

  it("does not spin the checklist's current step once the stage is blocked (nothing is actually running)", async () => {
    const run = makeRun({
      id: "sr-dev",
      stageName: "developer",
      status: "BlockedGate",
      gateChecks: [
        { id: "g1", stageRunId: "sr-dev", name: "context_bundle", passed: true, completedAt: "2026-01-01T00:00:01Z" },
        { id: "g2", stageRunId: "sr-dev", name: "verify_code", passed: false, completedAt: "2026-01-01T00:00:05Z" },
      ],
    });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [run],
    });
    await openDetail(release);

    const checklist = await screen.findByTestId("release-step-checklist");
    expect(within(checklist).getByTestId("release-step-code_hygiene")).toHaveAttribute("data-status", "pending");
    expect(within(checklist).queryByTestId("release-step-code_hygiene")).not.toHaveAttribute("data-status", "current");
  });

  it("shows the previous-stage reference panel and a live changed-files list for a non-first stage", async () => {
    const baRun = makeRun({ id: "sr-ba", stageName: "business-analyst", status: "Complete" });
    const devRun = makeRun({ id: "sr-dev", stageName: "developer", status: "Active" });
    const release = makeRelease({
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
      stageRuns: [baRun, devRun],
    });
    mockApi.getStageArtifactsAsync.mockResolvedValue([
      { relativePath: "devteam/features/f1/requirements.md", content: "## REQ-1: Add two numbers" },
    ]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([
      "back-end/DevTeam.Broker/Features/Calc/Calculator.cs",
      "back-end/DevTeam.Tests/Features/Calc/CalculatorTests.cs",
    ]);
    await openDetail(release);

    await waitFor(() => {
      expect(mockApi.getStageArtifactsAsync).toHaveBeenCalledWith(release.currentFeatureId, "sr-ba");
    });
    expect(await screen.findByText(/REQ-1: Add two numbers/)).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText("back-end/DevTeam.Broker/Features/Calc/Calculator.cs")).toBeInTheDocument();
    });
    expect(screen.getByText("back-end/DevTeam.Tests/Features/Calc/CalculatorTests.cs")).toBeInTheDocument();
  });

  it("does not show the previous-stage/changed-files panels for the first stage", async () => {
    const release = makeRelease({
      stageRuns: [makeRun({ id: "sr-ba", stageName: "business-analyst", status: "Active" })],
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    await openDetail(release);

    await waitFor(() => expect(screen.getByTestId("release-chat-input")).toBeInTheDocument());
    expect(mockApi.getWorkspaceChangesAsync).not.toHaveBeenCalled();
  });

  it("shows the stage-complete card with artifacts when the stage is ready to proceed", async () => {
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [makeRun({ status: "BlockedSignoff", phase: "Signoff", readyToProceed: true })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    mockApi.getStageArtifactsAsync.mockResolvedValue([
      { relativePath: "devteam/features/f1/handoff.md", content: "# Handoff\nAll set." },
    ]);
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    await openDetail(release);

    expect(await screen.findByTestId("release-stage-complete-card")).toBeInTheDocument();
    expect(await screen.findByText("devteam/features/f1/handoff.md")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Approve & move to Developer" })).toHaveLength(1);
    expect(mockApi.getReleaseAsync).toHaveBeenCalled();
  });

  it("does not show the stage-complete card when gates passed but DONE has not been said", async () => {
    const release = makeRelease({
      stageRuns: [makeRun({ status: "BlockedSignoff", phase: "Signoff", readyToProceed: false })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    await openDetail(release);

    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve & move to Developer" })).not.toBeInTheDocument();
  });

  it("does not show the review panel while the current stage run is not blocked on signoff", async () => {
    const release = makeRelease({
      stageRuns: [makeRun({ status: "Active" })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    await openDetail(release);

    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve & move to Developer" })).not.toBeInTheDocument();
  });

  it("does not show a review panel for another stage's pending signoff", async () => {
    const release = makeRelease({
      stageRuns: [makeRun({ id: "sr-qa", stageName: "qa", status: "BlockedSignoff", phase: "Signoff", readyToProceed: true })],
      signoffs: [{ id: "s2", releaseFeatureId: "f1", stageName: "qa", required: true, approved: false }],
    });
    await openDetail(release);

    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Approve & move to Developer" })).not.toBeInTheDocument();
  });

  it("clicking Proceed on the stage-complete card approves signoff and refreshes the release", async () => {
    const readyRun = makeRun({ status: "BlockedSignoff", phase: "Signoff", readyToProceed: true });
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [readyRun],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    const afterProceed = makeRelease({
      stageRuns: [makeRun({ stageName: "developer", status: "Active" })],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: true }],
      flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" },
    });
    mockApi.signoffFeatureAsync.mockResolvedValue(afterProceed);
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    const user = await openDetail(release);

    await screen.findByTestId("release-stage-complete-card");
    await user.click(screen.getByRole("button", { name: "Approve & move to Developer" }));

    await waitFor(() => {
      expect(mockApi.signoffFeatureAsync).toHaveBeenCalledWith(release.currentFeatureId, "business-analyst", "user", "Approved via wizard");
    });
    await waitFor(() => {
      expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    });
  });

  it("sending a new chat message after Continue hides the approve control once the release refreshes", async () => {
    const run = makeRun({ id: "sr-live", status: "BlockedSignoff", phase: "Signoff", readyToProceed: true });
    const release = makeRelease({
      status: "BlockedSignoff",
      stageRuns: [run],
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: false }],
    });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.sendStageMessageAsync.mockResolvedValue({ response: "ok", inputTokens: 1, outputTokens: 1, totalTokens: 2 });
    mockApi.getReleaseAsync.mockResolvedValue(release);
    const user = await openDetail(release);

    await screen.findByTestId("release-stage-complete-card");
    await user.click(screen.getByTestId("release-continue-btn"));
    await screen.findByTestId("release-approve-now-btn");

    const stillActive = makeRelease({
      stageRuns: [makeRun({ id: "sr-live", status: "Active", readyToProceed: false })],
      signoffs: release.signoffs,
    });
    mockApi.getReleaseAsync.mockResolvedValue(stillActive);

    await user.type(screen.getByTestId("release-chat-input"), "Actually, one more thing");
    await user.click(screen.getByTestId("release-send-btn"));

    await waitFor(() => {
      expect(mockApi.sendStageMessageAsync).toHaveBeenCalledWith(release.currentFeatureId, "Actually, one more thing");
    });
    await waitFor(() => {
      expect(screen.queryByTestId("release-approve-now-btn")).not.toBeInTheDocument();
    });
    expect(screen.queryByTestId("release-stage-complete-card")).not.toBeInTheDocument();
    expect(screen.getByTestId("release-chat-input")).toBeEnabled();
  });

  it("shows a model picker in the stage header and lets the user switch AI engine", async () => {
    const run = makeRun({ id: "sr-live", acpSessionId: "sess-1" });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([
      { value: "opencode/big-pickle", name: "OpenCode", description: null },
      { value: "claude-sonnet-4-5", name: "Claude", description: "Anthropic" },
    ]);
    mockApi.getSessionAsync.mockResolvedValue({
      sessionId: "sess-1", acpSessionId: "sess-1", workspacePath: "C:\\work\\proj",
      title: null, modelId: "opencode/big-pickle", modeId: null,
      createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
      models: [], modes: [], messages: [],
    });
    mockApi.setModelAsync.mockResolvedValue({ modelId: "claude-sonnet-4-5" });
    const user = await openDetail(makeRelease({ stageRuns: [run] }));

    const picker = await screen.findByTestId("release-model-picker");
    expect(within(picker).getByText("OpenCode")).toBeInTheDocument();
    expect(within(picker).getByText("Claude")).toBeInTheDocument();
    expect(picker).toHaveValue("opencode/big-pickle");

    await user.selectOptions(picker, "claude-sonnet-4-5");

    await waitFor(() => {
      expect(mockApi.setModelAsync).toHaveBeenCalledWith("sess-1", "claude-sonnet-4-5");
    });
    expect(picker).toHaveValue("claude-sonnet-4-5");
  });

  it("hides the model picker when no models are available", async () => {
    const run = makeRun({ id: "sr-live", acpSessionId: "sess-1" });
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    await openDetail(makeRelease({ stageRuns: [run] }));

    await waitFor(() => expect(mockApi.getAvailableModelsAsync).toHaveBeenCalled());
    expect(screen.queryByTestId("release-model-picker")).not.toBeInTheDocument();
  });

  it("reveals the workspace folder in the OS file explorer when clicked", async () => {
    mockApi.revealInExplorerAsync.mockResolvedValue(undefined);
    const user = await openDetail(makeRelease());

    await user.click(screen.getByTestId("release-reveal-workspace-btn"));

    await waitFor(() => {
      expect(mockApi.revealInExplorerAsync).toHaveBeenCalledWith("C:\\work\\proj");
    });
  });

  it("shows an inline error when revealing the workspace folder fails", async () => {
    mockApi.revealInExplorerAsync.mockRejectedValue(new Error("Directory does not exist"));
    const user = await openDetail(makeRelease());

    await user.click(screen.getByTestId("release-reveal-workspace-btn"));

    await waitFor(() => {
      expect(screen.getByText("Directory does not exist")).toBeInTheDocument();
    });
  });

  it("marks a stage as approved in the stepper once its signoff is approved", async () => {
    const release = makeRelease({
      signoffs: [{ id: "s1", releaseFeatureId: "f1", stageName: "business-analyst", required: true, approved: true }],
    });
    await openDetail(release);

    expect(screen.getByTestId("pipeline-step-business-analyst")).toHaveTextContent("· approved");
    expect(screen.getByTestId("pipeline-step-qa")).toHaveTextContent("· review needed");
  });

  describe("GitFlow feature lifecycle", () => {
    async function openDetailNoFeature(release: ReleaseDto) {
      const user = userEvent.setup();
      mockApi.listReleasesAsync.mockResolvedValue([release]);
      mockApi.getReleaseAsync.mockResolvedValue(release);
      render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);
      await waitFor(() => expect(screen.getByText("Release login-form")).toBeInTheDocument());
      await user.click(screen.getByTestId(`release-item-${release.id}`));
      await waitFor(() => expect(screen.getByTestId("release-new-feature-key")).toBeInTheDocument());
      return user;
    }

    it("shows a Create Feature form instead of the pipeline when no feature is in flight", async () => {
      const release = makeRelease({ currentFeatureId: null, features: [] });
      await openDetailNoFeature(release);

      expect(screen.queryByTestId("pipeline-stepper")).not.toBeInTheDocument();
      expect(screen.getByTestId("release-new-feature-key")).toBeInTheDocument();
      expect(screen.getByTestId("release-create-feature-btn")).toBeInTheDocument();
    });

    it("creates a feature and shows the pipeline for it once created", async () => {
      const release = makeRelease({ currentFeatureId: null, features: [] });
      const user = await openDetailNoFeature(release);

      const afterCreate = makeRelease();
      mockApi.createFeatureAsync.mockResolvedValue({
        id: "f1", releaseId: release.id, key: "password-reset", title: "password-reset",
        branchName: "feature/password-reset", status: "InProgress",
        createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
      });
      mockApi.getReleaseAsync.mockResolvedValue(afterCreate);
      mockApi.getPipelineAsync.mockResolvedValue(PIPELINE);

      await user.type(screen.getByTestId("release-new-feature-key"), "password-reset");
      await user.click(screen.getByTestId("release-create-feature-btn"));

      await waitFor(() => {
        expect(mockApi.createFeatureAsync).toHaveBeenCalledWith(release.id, "password-reset");
      });
      await waitFor(() => {
        expect(screen.getByTestId("pipeline-stepper")).toBeInTheDocument();
      });
    });

    it("shows an inline error when feature creation fails (e.g. one already in flight)", async () => {
      const release = makeRelease({ currentFeatureId: null, features: [] });
      const user = await openDetailNoFeature(release);
      mockApi.createFeatureAsync.mockRejectedValue(new Error("This release already has a feature in progress."));

      await user.type(screen.getByTestId("release-new-feature-key"), "password-reset");
      await user.click(screen.getByTestId("release-create-feature-btn"));

      await waitFor(() => {
        expect(screen.getByText("This release already has a feature in progress.")).toBeInTheDocument();
      });
    });

    it("lists completed features in a history section", async () => {
      const release = makeRelease({
        currentFeatureId: null,
        features: [
          { id: "f0", releaseId: "r1", key: "login-form", title: "login-form", branchName: "feature/login-form", status: "Complete", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z" },
        ],
      });
      await openDetailNoFeature(release);

      expect(screen.getByText("Completed features")).toBeInTheDocument();
      await userEvent.setup().click(screen.getByText("Completed features"));
      expect(screen.getByTestId("release-completed-feature-login-form")).toBeInTheDocument();
    });
  });
});