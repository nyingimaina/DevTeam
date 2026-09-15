import React from "react";
import { render, screen, waitFor, act } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ActiveTurnIndicator from "./ActiveTurnIndicator";
import BrokerApi from "../Chat/Data/BrokerApi";

jest.mock("../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

describe("ActiveTurnIndicator", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("renders nothing when no turn is active", async () => {
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    render(<ActiveTurnIndicator api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(mockApi.getCurrentTurnAsync).toHaveBeenCalled());
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("shows the turn preview and a cancel button when a turn is active", async () => {
    mockApi.getCurrentTurnAsync.mockResolvedValue({
      sessionId: "s1",
      acpSessionId: "acp-1",
      preview: "build a calculator",
      startedAt: new Date().toISOString(),
    });
    render(<ActiveTurnIndicator api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => {
      expect(screen.getByText(/build a calculator/)).toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: /cancel/i })).toBeInTheDocument();
  });

  it("calls cancelCurrentTurnAsync when the cancel button is clicked", async () => {
    const user = userEvent.setup();
    mockApi.getCurrentTurnAsync.mockResolvedValue({
      sessionId: "s1",
      acpSessionId: "acp-1",
      preview: "build a calculator",
      startedAt: new Date().toISOString(),
    });
    mockApi.cancelCurrentTurnAsync.mockResolvedValue(true);
    render(<ActiveTurnIndicator api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => screen.getByRole("button", { name: /cancel/i }));
    await user.click(screen.getByRole("button", { name: /cancel/i }));

    expect(mockApi.cancelCurrentTurnAsync).toHaveBeenCalledTimes(1);
  });

  it("polls periodically for the current turn", async () => {
    jest.useFakeTimers({ legacyFakeTimers: false });
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    render(<ActiveTurnIndicator api={mockApi as unknown as BrokerApi} />);

    await act(async () => {
      await Promise.resolve();
    });
    expect(mockApi.getCurrentTurnAsync).toHaveBeenCalledTimes(1);

    await act(async () => {
      jest.advanceTimersByTime(4000);
      await Promise.resolve();
    });
    expect(mockApi.getCurrentTurnAsync.mock.calls.length).toBeGreaterThanOrEqual(2);

    jest.useRealTimers();
  });
});
