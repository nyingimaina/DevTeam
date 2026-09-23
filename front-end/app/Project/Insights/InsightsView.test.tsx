import React from "react";
import { render, screen } from "@testing-library/react";
import "@testing-library/jest-dom";
import InsightsView from "./InsightsView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { MetricsSummaryDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

function summary(overrides: Partial<MetricsSummaryDto> = {}): MetricsSummaryDto {
  return {
    schemaVersion: 1,
    generatedAtUtc: "2026-09-22T00:00:00Z",
    scope: { workspacePath: "D:\\apps\\CalcV4", days: 30 },
    totals: { turns: 12, inputTokens: 900, outputTokens: 100, totalTokens: 1000, cachedReadTokens: 0, durationMs: 120_000, costAmount: null },
    perStage: [
      { stageName: "developer", turns: 8, attempts: 3, totalTokens: 800, retryTokens: 300, challengeTokens: 100, durationMs: 90_000, gateFailures: 2 },
    ],
    perKind: [],
    perModel: [],
    promptSections: [
      { section: "artifact", totalChars: 8000, avgChars: 800, percentOfPrompt: 0.8 },
      { section: "base", totalChars: 2000, avgChars: 200, percentOfPrompt: 0.2 },
    ],
    findings: [
      { id: "retry-token-share", severity: "high", title: "38% of the developer stage's tokens were retries", evidence: {}, suggestedAction: "Fix the check." },
    ],
    notes: ["All times are UTC."],
    ...overrides,
  };
}

function renderView() {
  return render(<InsightsView api={mockApi as unknown as BrokerApi} workspacePath="D:\\apps\\CalcV4" />);
}

describe("InsightsView", () => {
  beforeEach(() => jest.clearAllMocks());

  it("shows totals, findings, stage cost and prompt sections", async () => {
    mockApi.getMetricsSummaryAsync.mockResolvedValue(summary());

    renderView();

    expect(await screen.findByTestId("insights-view")).toBeInTheDocument();
    expect(screen.getByTestId("insights-totals")).toHaveTextContent("1,000 tokens");
    expect(screen.getByTestId("insight-retry-token-share")).toHaveTextContent("Fix the check.");
    expect(screen.getByTestId("insights-stages")).toHaveTextContent("developer");
    expect(screen.getByTestId("insights-sections")).toHaveTextContent("artifact");
  });

  it("says so when nothing looks wasteful", async () => {
    mockApi.getMetricsSummaryAsync.mockResolvedValue(summary({ findings: [] }));

    renderView();

    expect(await screen.findByTestId("insights-no-findings")).toBeInTheDocument();
  });

  it("shows an error when the report can't load", async () => {
    mockApi.getMetricsSummaryAsync.mockRejectedValue(new Error("boom"));

    renderView();

    expect(await screen.findByTestId("insights-error")).toBeInTheDocument();
  });
});
