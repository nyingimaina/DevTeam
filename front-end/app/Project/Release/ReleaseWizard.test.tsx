import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ReleaseWizard from "./ReleaseWizard";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

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

describe("ReleaseWizard", () => {
  beforeEach(() => jest.clearAllMocks());

  it("loads and displays releases on mount", async () => {
    const releases = [makeRelease({ id: "r1" }), makeRelease({ id: "r2", title: "Release feat-b" })];
    mockApi.listReleasesAsync.mockResolvedValue(releases);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath="C:\\work\\proj" />);

    await waitFor(() => {
      expect(screen.getByText("Release login-form")).toBeInTheDocument();
    });
    expect(screen.getByText("Release feat-b")).toBeInTheDocument();
    expect(mockApi.listReleasesAsync).toHaveBeenCalledTimes(1);
  });

  it("shows empty message when no releases exist", async () => {
    mockApi.listReleasesAsync.mockResolvedValue([]);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath="C:\\work\\proj" />);

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

  it("clicking a release navigates to detail view", async () => {
    const user = userEvent.setup();
    const release = makeRelease({ id: "r1" });
    mockApi.listReleasesAsync.mockResolvedValue([release]);
    mockApi.getReleaseAsync.mockResolvedValue(release);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath="C:\\work\\proj" />);

    await waitFor(() => {
      expect(screen.getByText("Release login-form")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("release-item-r1"));

    await waitFor(() => {
      expect(screen.getByText(/business-analyst/)).toBeInTheDocument();
    });
  });

  it("advance button triggers advanceReleaseAsync", async () => {
    const user = userEvent.setup();
    const release = makeRelease();
    mockApi.listReleasesAsync.mockResolvedValue([release]);
    const advanced = makeRelease({ status: "Blocked" });
    mockApi.advanceReleaseAsync.mockResolvedValue(advanced);
    mockApi.getReleaseAsync.mockResolvedValue(release);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath="C:\\work\\proj" />);

    await waitFor(() => {
      expect(screen.getByText("Release login-form")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("release-item-00000000-0000-0000-0000-000000000001"));

    await waitFor(() => {
      expect(screen.getByTestId("release-advance-btn")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("release-advance-btn"));

    await waitFor(() => {
      expect(mockApi.advanceReleaseAsync).toHaveBeenCalledWith("00000000-0000-0000-0000-000000000001");
    });
  });

  it("displays gate check results", async () => {
    const release = makeRelease({
      stageRuns: [{
        id: "sr1",
        releaseId: "r1",
        stageName: "business-analyst",
        status: "Complete",
        summary: "All gates passed.",
        attempt: 1,
        gateChecks: [
          { id: "gc1", stageRunId: "sr1", name: "scaffold_specs", passed: true, evidenceText: "12 tests, all green" },
          { id: "gc2", stageRunId: "sr1", name: "context_bundle", passed: false, evidenceText: "Missing context" },
        ],
      }],
    });
    mockApi.listReleasesAsync.mockResolvedValue([]);
    mockApi.getReleaseAsync.mockResolvedValue(release);

    const user = userEvent.setup();
    mockApi.listReleasesAsync.mockResolvedValue([release]);

    render(<ReleaseWizard api={mockApi as unknown as BrokerApi} workspacePath="C:\\work\\proj" />);

    await waitFor(() => {
      expect(screen.getByText("Release login-form")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("release-item-00000000-0000-0000-0000-000000000001"));

    await waitFor(() => {
      expect(screen.getByText("scaffold_specs")).toBeInTheDocument();
    });
    expect(screen.getByText("12 tests, all green")).toBeInTheDocument();
    expect(screen.getByText("context_bundle")).toBeInTheDocument();
    expect(screen.getByText("Missing context")).toBeInTheDocument();
  });
});
