import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import AttentionBar from "./AttentionBar";
import BrokerApi from "../Chat/Data/BrokerApi";
import { AttentionItemDto } from "../Chat/Data/BrokerTypes";
import * as alarm from "./attentionAlarm";

jest.mock("../Chat/Data/BrokerApi");
jest.mock("./attentionAlarm", () => ({
  ...jest.requireActual("./attentionAlarm"),
  playAttentionChime: jest.fn(),
}));

const mockApi = jest.mocked(BrokerApi.prototype);
const chime = jest.mocked(alarm.playAttentionChime);

const approval: AttentionItemDto = {
  id: "run-1",
  releaseId: "rel-1",
  featureId: "feat-1",
  featureKey: "login",
  stageName: "business-analyst",
  kind: "Approval",
  title: "Your approval is needed",
  message: 'The Business Analyst step for "login" is finished. Look it over and approve it to let the work continue.',
  since: new Date().toISOString(),
};

const decision: AttentionItemDto = {
  ...approval,
  id: "run-2",
  kind: "Decision",
  title: "The team needs a decision from you",
  message: '"login" is paused at the Test-runner step. This needs an operator decision before it can continue.',
};

function renderBar(onOpen = jest.fn()) {
  render(<AttentionBar api={mockApi as unknown as BrokerApi} workspacePath="C:/proj" onOpen={onOpen} />);
  return onOpen;
}

describe("AttentionBar", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    document.title = "DevTeam";
    mockApi.getNotificationSettingsAsync.mockResolvedValue({
      stageComplete: true,
      needsAttention: true,
      approvalNeeded: true,
      sound: true,
    });
  });

  it("renders nothing while nothing is waiting on the person", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([]);
    renderBar();

    await waitFor(() => expect(mockApi.getAttentionAsync).toHaveBeenCalledWith("C:/proj"));
    expect(screen.queryByTestId("attention-bar")).not.toBeInTheDocument();
    expect(chime).not.toHaveBeenCalled();
  });

  it("says plainly what is waiting, as an alert", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    renderBar();

    const bar = await screen.findByRole("alert");
    expect(bar).toHaveTextContent("Your approval is needed");
    expect(bar).toHaveTextContent("Look it over and approve it");
  });

  it("cannot be dismissed - its only button is the way through", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    renderBar();

    await screen.findByRole("alert");
    expect(screen.getAllByRole("button")).toHaveLength(1);
    expect(screen.queryByRole("button", { name: /dismiss|close|hide/i })).not.toBeInTheDocument();
  });

  it("offers an approval and a decision different, plain-worded actions", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    const { unmount } = render(
      <AttentionBar api={mockApi as unknown as BrokerApi} workspacePath="C:/proj" onOpen={jest.fn()} />,
    );
    expect(await screen.findByRole("button", { name: "Review and approve" })).toBeInTheDocument();
    unmount();

    mockApi.getAttentionAsync.mockResolvedValue([decision]);
    renderBar();
    expect(await screen.findByRole("button", { name: "See what's needed" })).toBeInTheDocument();
  });

  it("takes the person to the waiting item when the button is pressed", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    const onOpen = renderBar();

    await userEvent.click(await screen.findByRole("button", { name: "Review and approve" }));

    expect(onOpen).toHaveBeenCalledWith(approval);
  });

  it("mentions anything else that is also waiting", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval, decision]);
    renderBar();

    expect(await screen.findByText(/1 more thing is also waiting/)).toBeInTheDocument();
  });

  it("sounds the alarm once for a new request, not on every poll", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    renderBar();

    await screen.findByRole("alert");
    await waitFor(() => expect(chime).toHaveBeenCalledTimes(1));
  });

  it("stays quiet when sound is switched off in settings", async () => {
    mockApi.getNotificationSettingsAsync.mockResolvedValue({
      stageComplete: true,
      needsAttention: true,
      approvalNeeded: true,
      sound: false,
    });
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    renderBar();

    await screen.findByRole("alert");
    expect(chime).not.toHaveBeenCalled();
  });

  it("still shows the request when the settings cannot be read", async () => {
    mockApi.getNotificationSettingsAsync.mockRejectedValue(new Error("offline"));
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    renderBar();

    expect(await screen.findByRole("alert")).toBeInTheDocument();
    await waitFor(() => expect(chime).toHaveBeenCalledTimes(1));
  });

  it("marks the window title so another window or tab can tell, and restores it afterwards", async () => {
    mockApi.getAttentionAsync.mockResolvedValue([approval]);
    const { unmount } = render(
      <AttentionBar api={mockApi as unknown as BrokerApi} workspacePath="C:/proj" onOpen={jest.fn()} />,
    );

    await screen.findByRole("alert");
    expect(document.title).toBe("(1) Waiting for you — DevTeam");

    unmount();
    expect(document.title).toBe("DevTeam");
  });

  describe("rulings on challenged tests", () => {
    const withRulings: AttentionItemDto = {
      ...decision,
      message: '"login" is paused at the Test-runner step. The test checker believes a test is wrong and needs your ruling.',
      rulings: [
        {
          test: "Wizard > saves",
          requirement: "REQ-4",
          expected: "settings persist",
          observed: "the test asserts a toast nobody asked for",
          details: "Proposed change: drop the toast assertion",
        },
      ],
    };

    it("puts the question in plain words with both answers as buttons", async () => {
      mockApi.getAttentionAsync.mockResolvedValue([withRulings]);
      renderBar();

      const bar = await screen.findByTestId("attention-bar");
      expect(bar).toHaveTextContent("Wizard > saves");
      expect(bar).toHaveTextContent("settings persist");
      expect(bar).toHaveTextContent("the test asserts a toast nobody asked for");
      expect(screen.getByRole("button", { name: /the test is wrong/i })).toBeInTheDocument();
      expect(screen.getByRole("button", { name: /the requirement is right/i })).toBeInTheDocument();
    });

    it("records an accept ruling, then carries on once it was the last question", async () => {
      mockApi.getAttentionAsync.mockResolvedValue([withRulings]);
      mockApi.recordRulingAsync.mockResolvedValue({ ok: true });
      mockApi.runStageAsync.mockResolvedValue({} as never);
      renderBar();

      await userEvent.click(await screen.findByRole("button", { name: /the test is wrong/i }));

      await waitFor(() =>
        expect(mockApi.recordRulingAsync).toHaveBeenCalledWith("feat-1", "Wizard > saves", "accept"));
      await waitFor(() => expect(mockApi.runStageAsync).toHaveBeenCalledWith("feat-1"));
    });

    it("records a reject ruling when the requirement is kept", async () => {
      mockApi.getAttentionAsync.mockResolvedValue([withRulings]);
      mockApi.recordRulingAsync.mockResolvedValue({ ok: true });
      mockApi.runStageAsync.mockResolvedValue({} as never);
      renderBar();

      await userEvent.click(await screen.findByRole("button", { name: /the requirement is right/i }));

      await waitFor(() =>
        expect(mockApi.recordRulingAsync).toHaveBeenCalledWith("feat-1", "Wizard > saves", "reject"));
    });

    it("waits for the remaining questions before carrying on", async () => {
      const two: AttentionItemDto = {
        ...withRulings,
        rulings: [...withRulings.rulings!, { ...withRulings.rulings![0], test: "Wizard > loads" }],
      };
      mockApi.getAttentionAsync.mockResolvedValue([two]);
      mockApi.recordRulingAsync.mockResolvedValue({ ok: true });
      renderBar();

      await userEvent.click((await screen.findAllByRole("button", { name: /the test is wrong/i }))[0]);

      await waitFor(() => expect(mockApi.recordRulingAsync).toHaveBeenCalledTimes(1));
      expect(mockApi.runStageAsync).not.toHaveBeenCalled();
    });

    it("says so, in plain words, when a ruling could not be saved", async () => {
      mockApi.getAttentionAsync.mockResolvedValue([withRulings]);
      mockApi.recordRulingAsync.mockRejectedValue(new Error("boom"));
      renderBar();

      await userEvent.click(await screen.findByRole("button", { name: /the test is wrong/i }));

      expect(await screen.findByText(/could not save your answer/i)).toBeInTheDocument();
      expect(mockApi.runStageAsync).not.toHaveBeenCalled();
    });
  });
});
