import React from "react";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ChecksView from "./ChecksView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import {
  CheckDefinitionDto,
  ReadinessCheckDto,
  ReadinessReportDto,
  ReleaseDto,
} from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
jest.mock("recharts", () => {
  const Passthrough = ({ children }: { children?: React.ReactNode }) => <div>{children}</div>;
  return {
    ResponsiveContainer: Passthrough,
    BarChart: Passthrough,
    LineChart: Passthrough,
    PieChart: Passthrough,
    Pie: Passthrough,
    Bar: () => null,
    Line: () => null,
    Cell: () => null,
    CartesianGrid: () => null,
    Tooltip: () => null,
    XAxis: () => null,
    YAxis: () => null,
  };
});

const mockApi = jest.mocked(BrokerApi.prototype);
const WORKSPACE = "D:\\apps\\calculator";

// The internal words a person must never see in this app's copy.
const BANNED = /checkout|stash|branch|HEAD|gate|gherkin|OnHold|BlockedGate|BlockedSignoff|Proposed/i;

function checkDefinition(o: Partial<CheckDefinitionDto> = {}): CheckDefinitionDto {
  return {
    id: "backend-unit",
    title: "The backend tests pass",
    category: "backend",
    whyItMatters: "Tests describe the behaviour we promised.",
    howToFix: "Make the failing tests pass.",
    required: true,
    scope: "project",
    technical: "dotnet test DevTeam.slnx",
    ...o,
  };
}

function checkResult(o: Partial<ReadinessCheckDto> = {}): ReadinessCheckDto {
  return {
    phaseId: "backend-unit",
    title: "The backend tests pass",
    status: "Passed",
    reason: "Passed.",
    durationMs: 1200,
    metrics: { testsPassed: 42 },
    rawOutput: "Passed! - Failed: 0, Passed: 42",
    ...o,
  };
}

function report(o: Partial<ReadinessReportDto> = {}): ReadinessReportDto {
  return {
    id: "rep1",
    releaseId: "rel",
    scope: "release",
    passed: true,
    startedAt: "2026-01-10T12:00:00Z",
    durationMs: 60_000,
    releaseVersion: "1.0.0",
    checks: [checkResult()],
    failedCount: 0,
    skippedCount: 0,
    blockerSummary: "Every check passed.",
    ...o,
  };
}

function release(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel",
    workspacePath: WORKSPACE,
    title: "Release adding",
    version: "1.0.0",
    status: "InProgress",
    branchName: "release/adding",
    currentFeatureId: null,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-10T10:00:00Z",
    features: [],
    stageRuns: [],
    signoffs: [],
    flowPosition: null,
    ...o,
  };
}

function renderView() {
  return render(<ChecksView api={mockApi as unknown as BrokerApi} workspacePath={WORKSPACE} />);
}

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.getChecksAsync.mockResolvedValue([checkDefinition()]);
  mockApi.listReleasesAsync.mockResolvedValue([release()]);
  mockApi.getReadinessAsync.mockResolvedValue(report());
  mockApi.getReadinessHistoryAsync.mockResolvedValue([report()]);
  mockApi.runReadinessAsync.mockResolvedValue(report());
});

describe("ChecksView", () => {
  it("lists the checks this project must pass, with why and what to do", async () => {
    mockApi.getChecksAsync.mockResolvedValue([
      checkDefinition(),
      checkDefinition({
        id: "frontend-lint",
        title: "The code follows the project's house rules",
        category: "frontend",
        whyItMatters: "Consistent style keeps the codebase cheap to change.",
        howToFix: "Apply the house rules, or agree an exception.",
        technical: "npx next lint",
      }),
    ]);

    const { container } = renderView();

    const catalog = await screen.findByTestId("checks-catalog");
    expect(within(catalog).getByText("The backend tests pass")).toBeInTheDocument();
    expect(within(catalog).getByText("Consistent style keeps the codebase cheap to change.")).toBeInTheDocument();
    expect(within(catalog).getByText(/Apply the house rules/)).toBeInTheDocument();
    // The raw command is only behind a technical-details disclosure.
    expect(within(catalog).getByText("dotnet test DevTeam.slnx")).toBeInTheDocument();
    expect(container.textContent ?? "").not.toMatch(BANNED);
  });

  it("shows the latest verdict and the individual results", async () => {
    renderView();

    const verdict = await screen.findByTestId("checks-verdict");
    expect(verdict).toHaveTextContent("Everything passed");
    const results = await screen.findByTestId("checks-results");
    expect(within(results).getByTestId("check-result-backend-unit")).toHaveTextContent("The backend tests pass");
    expect(within(results).getByText("42 tests passed")).toBeInTheDocument();
  });

  it("explains a failed verdict in plain language", async () => {
    mockApi.getReadinessAsync.mockResolvedValue(
      report({
        passed: false,
        failedCount: 1,
        checks: [checkResult({ status: "Failed", reason: "3 tests didn't pass." })],
        blockerSummary: "The final checks didn't all pass, so this wasn't shipped:\n- The backend tests pass: 3 tests didn't pass.",
      }),
    );

    renderView();

    const verdict = await screen.findByTestId("checks-verdict");
    expect(verdict).toHaveTextContent("Some checks didn't pass");
    expect(verdict).toHaveTextContent("3 tests didn't pass.");
  });

  it("runs the checks on demand and shows the fresh result", async () => {
    renderView();
    await screen.findByTestId("checks-verdict");

    await userEvent.click(screen.getByTestId("checks-run-btn"));

    expect(mockApi.runReadinessAsync).toHaveBeenCalledWith("rel");
    await waitFor(() => expect(screen.getByTestId("checks-verdict")).toHaveTextContent("Everything passed"));
  });

  it("tells the user it is working, with an estimate, instead of looking hung", async () => {
    let resolveRun: (value: ReadinessReportDto) => void = () => {};
    mockApi.runReadinessAsync.mockImplementation(
      () => new Promise<ReadinessReportDto>((resolve) => {
        resolveRun = resolve;
      }),
    );
    // Two prior runs give the estimate something to learn from.
    mockApi.getReadinessHistoryAsync.mockResolvedValue([
      report({ id: "a", durationMs: 60_000 }),
      report({ id: "b", durationMs: 60_000 }),
    ]);

    renderView();
    await screen.findByTestId("checks-verdict");

    await userEvent.click(screen.getByTestId("checks-run-btn"));

    const running = await screen.findByTestId("checks-running");
    expect(running).toHaveTextContent(/about a minute/);

    resolveRun(report());
    await waitFor(() => expect(screen.queryByTestId("checks-running")).not.toBeInTheDocument());
  });

  it("says there is nothing to check rather than showing an empty library", async () => {
    mockApi.getChecksAsync.mockResolvedValue([]);

    renderView();

    expect(await screen.findByTestId("checks-empty")).toBeInTheDocument();
  });

  it("asks the user to start a release before checks can run", async () => {
    mockApi.listReleasesAsync.mockResolvedValue([]);

    renderView();

    expect(await screen.findByTestId("checks-no-release")).toBeInTheDocument();
    expect(screen.queryByTestId("checks-run-btn")).not.toBeInTheDocument();
  });
});
