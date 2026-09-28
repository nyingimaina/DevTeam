import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import { StageScreen } from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

// The exact body the broker returns when it cannot start the CLI. The user has to be able to read
// this after the fact - it is the only place the reason and the request reference are written down.
const LAUNCH_REFUSED =
  "The opencode command-line tool is installed but Windows refused to start it " +
  '(Win32 error 448, "The path cannot be traversed because it contains an untrusted mount point."). ' +
  "This is almost always antivirus or a locked-down mount point, not a broken install - unblock the " +
  "file or ask your administrator to allow it, then send the message again. " +
  "(reference: 6b9991baedcf)";

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: [], steps: ["scaffold_specs", "agent:business-analyst"] },
];

function makeRun(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr1", releaseFeatureId: "f1", stageName: "business-analyst", status: "Active", phase: "GuidedQA", questionCount: 1,
    attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [],
    specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp-1", ...o,
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

async function sendAndFail(user: ReturnType<typeof userEvent.setup>) {
  await user.type(await screen.findByTestId("release-chat-input"), "add a login form");
  await user.click(screen.getByTestId("release-send-btn"));
}

describe("StageScreen when the broker refuses to send", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getStageMessagesAsync.mockResolvedValue([]);
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getSessionAsync.mockResolvedValue({ modelId: "a/one" } as never);
    mockApi.sendStageMessageAsync.mockRejectedValue(new Error(LAUNCH_REFUSED));
  });

  it("shows the broker's explanation and its request reference", async () => {
    renderStage(makeRelease(), makeRun());

    await sendAndFail(userEvent.setup());

    expect(await screen.findByText(/Windows refused to start it/i)).toBeInTheDocument();
    expect(screen.getByText(/reference: 6b9991baedcf/i)).toBeInTheDocument();
  });

  it("still shows it after the transcript poller has ticked", async () => {
    // The bug. The transcript poll runs every 2.5s and it used to call setMessagesError(null) on every
    // success. Reading the transcript says nothing about whether a send worked - and this read always
    // succeeds, because the refusal was in starting the agent, not in fetching messages. So the
    // explanation was wiped within one poll, and since the input had already been cleared and no
    // message was ever stored, the user's message looked like it had simply been dropped.
    renderStage(makeRelease(), makeRun());

    await sendAndFail(userEvent.setup());
    await screen.findByText(/Windows refused to start it/i);

    await new Promise((r) => setTimeout(r, 3200)); // one poll tick plus slack

    expect(screen.getByText(/Windows refused to start it/i)).toBeInTheDocument();
    expect(screen.getByText(/reference: 6b9991baedcf/i)).toBeInTheDocument();
  });

  it("puts the message back in the box and takes the unsent copy out of the transcript", async () => {
    // A rejected message was never stored, so the optimistic copy has to go - leaving it there
    // claims the agent received it. Putting the text back means the fix for a refused send is to
    // click Send again, not to retype what was already typed.
    renderStage(makeRelease(), makeRun());
    const user = userEvent.setup();

    await sendAndFail(user);
    await screen.findByText(/Windows refused to start it/i);

    expect(screen.queryByText("add a login form")).not.toBeInTheDocument();
    const box = screen.getByTestId("release-chat-input") as HTMLInputElement;
    expect(box.value).toBe("add a login form");
    // The box has to be usable again, or the refusal is just a dead end.
    await waitFor(() => expect(box).toBeEnabled());
  });

  it("retires the refusal once the message is sent for real", async () => {
    renderStage(makeRelease(), makeRun());
    const user = userEvent.setup();
    await sendAndFail(user);
    await screen.findByTestId("release-send-error");

    mockApi.sendStageMessageAsync.mockResolvedValue({ messages: [] } as never);
    // Enter rather than the button: the button carries the design system's own post-click lock, so
    // clicking it again in a test measures that lock, not whether a second attempt is honoured.
    const box = screen.getByTestId("release-chat-input");
    await waitFor(() => expect(box).toBeEnabled());
    await user.type(box, "{Enter}");

    await waitFor(() => expect(screen.queryByTestId("release-send-error")).not.toBeInTheDocument());
  });

  it("keeps the explanation when the background poll re-renders the panel", async () => {
    // The parent polls the release every 3s and hands down a fresh object. A complaint that the error
    // "flashes and then disappears" is exactly this: a re-render between the refusal and the next
    // look. The reason has to survive it, because nothing will ever repeat the failure for the user
    // to see again.
    const run = makeRun();
    const { rerender } = renderStage(makeRelease(), run);

    await sendAndFail(userEvent.setup());
    await screen.findByText(/Windows refused to start it/i);

    rerender(
      <StageScreen
        release={makeRelease({ ...run, questionCount: 2 }, { updatedAt: "2026-01-01T00:00:09Z" })}
        featureId="f1"
        api={mockApi as unknown as BrokerApi}
        pipeline={PIPELINE}
        role={PIPELINE[0]}
        run={run}
        testIdPrefix="release"
        refreshRelease={async () => undefined}
      />,
    );

    await waitFor(() =>
      expect(screen.getByText(/Windows refused to start it/i)).toBeInTheDocument(),
    );
  });
});
