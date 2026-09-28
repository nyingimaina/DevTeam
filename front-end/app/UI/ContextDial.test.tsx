import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ContextDial from "./ContextDial";
import BrokerApi from "../Chat/Data/BrokerApi";
import type { ContextDto } from "../Chat/Data/ContextTypes";

jest.mock("../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

const reading = (over: Partial<ContextDto> = {}): ContextDto => ({
  sessionId: "s1",
  usedTokens: 0,
  contextSize: 200_000,
  deltaTokens: null,
  costAmount: null,
  costCurrency: null,
  turnActive: false,
  compactionCount: 0,
  updatedAt: "2026-09-28T10:00:00Z",
  ...over,
});

describe("ContextDial", () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it("shows the share of the window in the middle of the dial", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 50_000 }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("25%");
  });

  it("says the context is unmeasured rather than claiming an empty dial", async () => {
    // The first honest state is "no reading yet". A dial reading 0% would claim the context was
    // measured and found empty, which nothing supports on a first turn.
    mockApi.getContextAsync.mockResolvedValue(reading({ contextSize: null, updatedAt: null }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("–");
    expect(screen.getByText(/not measured/i)).toBeInTheDocument();
  });

  it("fills the ring in proportion to the context used", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 150_000 }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial-arc")).toBeInTheDocument());
    // 75% of a 2πr ring: the dash covers three quarters of the circumference.
    const arc = screen.getByTestId("context-dial-arc");
    const dash = Number(arc.getAttribute("stroke-dasharray")?.split(" ")[0] ?? "0");
    const circumference = Number(arc.getAttribute("data-circumference"));
    expect(dash / circumference).toBeCloseTo(0.75, 2);
  });

  it("shows a nearly empty context as a closed ring rather than a negative one", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 0 }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial-arc")).toBeInTheDocument());
    const dash = Number(
      screen.getByTestId("context-dial-arc").getAttribute("stroke-dasharray")?.split(" ")[0] ?? "-1");
    expect(dash).toBeGreaterThanOrEqual(0);
    expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("0%");
  });

  it("marks the reading as in progress so a stale number is not read as current", async () => {
    // The agent reports once at the end of a turn. While a turn is running the number shown is from
    // the previous one, and saying so is the difference between a reading and a live gauge.
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 50_000, turnActive: true }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByText(/turn in progress/i)).toBeInTheDocument());
  });

  it("compacts on a double click", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 180_000 }));
    mockApi.compactContextAsync.mockResolvedValue({
      usedTokensBefore: 180_000,
      usedTokensAfter: 40_000,
      freedTokens: 140_000,
      durationMs: 2_100,
      stopReason: "end_turn",
      context: reading({ usedTokens: 40_000 }),
    });
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("90%"));
    await userEvent.dblClick(screen.getByTestId("context-dial"));

    await waitFor(() => expect(mockApi.compactContextAsync).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByText(/freed 140,000/i)).toBeInTheDocument());
  });

  it("can also be compacted from the keyboard, because a double click cannot be", async () => {
    // A double click is invisible to keyboard and screen-reader users. If compacting is reachable
    // only that way, it is not a feature so much as a gesture.
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 180_000 }));
    mockApi.compactContextAsync.mockResolvedValue({
      usedTokensBefore: 180_000,
      usedTokensAfter: 40_000,
      freedTokens: 140_000,
      durationMs: 1_500,
      stopReason: "end_turn",
      context: reading({ usedTokens: 40_000 }),
    });
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("90%"));
    screen.getByTestId("context-dial").focus();
    await userEvent.keyboard("{Enter}");

    await waitFor(() => expect(mockApi.compactContextAsync).toHaveBeenCalledTimes(1));
  });

  it("ignores a single click, so the dial is not compacted by a stray miss", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 180_000 }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    await userEvent.click(screen.getByTestId("context-dial"));

    expect(mockApi.compactContextAsync).not.toHaveBeenCalled();
  });

  it("says so when a compaction freed almost nothing, instead of implying it helped", async () => {
    // A context that is mostly irreducible instruction does not shrink much however many times it is
    // summarized. Reporting that plainly is more useful than a green tick: the answer is a different
    // feature, not another compaction.
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 180_000 }));
    mockApi.compactContextAsync.mockResolvedValue({
      usedTokensBefore: 180_000,
      usedTokensAfter: 172_000,
      freedTokens: 8_000,
      durationMs: 1_900,
      stopReason: "end_turn",
      context: reading({ usedTokens: 172_000 }),
    });
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("90%"));
    await userEvent.dblClick(screen.getByTestId("context-dial"));

    await waitFor(() => expect(screen.getByText(/barely any room/i)).toBeInTheDocument());
  });

  it("surfaces a failed compaction rather than leaving the dial unchanged with no explanation", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 180_000 }));
    mockApi.compactContextAsync.mockRejectedValue(new Error("the agent is not responding"));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial")).toBeInTheDocument());
    await waitFor(() => expect(screen.getByTestId("context-dial-percent")).toHaveTextContent("90%"));
    await userEvent.dblClick(screen.getByTestId("context-dial"));

    await waitFor(() => expect(screen.getByRole("alert")).toBeInTheDocument());
  });

  it("keeps the gradient as a single definition so the arc cannot be drawn ungradiented", async () => {
    mockApi.getContextAsync.mockResolvedValue(reading({ usedTokens: 90_000 }));
    render(<ContextDial api={mockApi as unknown as BrokerApi} />);

    await waitFor(() => expect(screen.getByTestId("context-dial-arc")).toBeInTheDocument());
    const arc = screen.getByTestId("context-dial-arc");
    const paint = arc.getAttribute("stroke");
    expect(paint).toMatch(/url\(/);
  });
});
