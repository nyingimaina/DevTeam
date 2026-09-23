import React from "react";
import { fireEvent, render, screen, within, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import ProjectHome from "./ProjectHome";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const NOW = new Date("2026-01-10T12:00:00Z");
const PIPELINE = ["business-analyst", "developer", "qa"];
const WORKSPACE = "D:\\apps\\calculator";

function run(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr", releaseFeatureId: "f", stageName: "developer", status: "Active", phase: "Producing", questionCount: 0, attempt: 1,
    startedAt: "2026-01-10T10:00:00Z", readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [],
    specialistConsultations: [], lastErrorKind: "None", ...o,
  };
}

function feat(o: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f1", releaseId: "rel", key: "subtraction", title: "subtraction", branchName: "feature/subtraction", status: "InProgress",
    createdAt: "2026-01-09T00:00:00Z", updatedAt: "2026-01-10T10:00:00Z", stageRuns: [run()], signoffs: [],
    flowPosition: { id: "fp", releaseFeatureId: "f1", currentStageIndex: 1, currentStageName: "developer" }, ...o,
  };
}

const ADDING = feat({ id: "f0", key: "adding", status: "Complete", updatedAt: "2026-01-05T00:00:00Z", stageRuns: [], flowPosition: null });

function rel(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel", workspacePath: WORKSPACE, title: "Release adding", version: "0.1.0", status: "InProgress", branchName: "release/adding",
    currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-10T10:00:00Z",
    features: [ADDING, feat()], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

function hotfix(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "hot", workspacePath: WORKSPACE, title: "Urgent fix div-by-zero", version: "0.1.0", status: "InProgress", branchName: "main",
    isHotfix: true, currentFeatureId: "hf1", createdAt: "2026-01-10T00:00:00Z", updatedAt: "2026-01-10T10:00:00Z",
    features: [feat({ id: "hf1", key: "div-by-zero" })], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

function renderHome(releases: ReleaseDto[], extra: Partial<React.ComponentProps<typeof ProjectHome>> = {}) {
  const onReleaseCreated = jest.fn();
  const onRetry = jest.fn();
  render(
    <ProjectHome
      api={mockApi as unknown as BrokerApi}
      workspacePath={WORKSPACE}
      releases={releases}
      loading={false}
      error={null}
      onRetry={onRetry}
      onReleaseCreated={onReleaseCreated}
      now={NOW}
      {...extra}
    />,
  );
  return { onReleaseCreated, onRetry };
}

describe("ProjectHome", () => {
  beforeEach(() => jest.clearAllMocks());

  it("shows every open release and urgent fix as a folder that links into it", () => {
    renderHome([rel(), hotfix()]);

    const releaseFolder = screen.getByTestId("project-folder-rel");
    const fixFolder = screen.getByTestId("project-folder-hot");
    expect(releaseFolder).toHaveAttribute("href", "#/r/rel");
    expect(fixFolder).toHaveAttribute("href", "#/r/hot");
    expect(within(releaseFolder).getByText("Release")).toBeInTheDocument();
    expect(within(releaseFolder).getByText("1 of 2 features done")).toBeInTheDocument();
    expect(within(releaseFolder).getByText("2 hours ago")).toBeInTheDocument();
    expect(within(fixFolder).getByText("Urgent fix")).toBeInTheDocument();
  });

  it("marks a hotfix folder as an urgent fix, and a release as a release", () => {
    renderHome([rel(), hotfix()]);
    // The kind word is the accessible distinction; the stylesheet adds the red accent.
    expect(screen.getAllByText("Release").length).toBeGreaterThan(0);
    expect(screen.getAllByText("Urgent fix").length).toBeGreaterThan(0);
  });

  it("shows a friendly call to action when there is nothing open yet", () => {
    renderHome([]);
    expect(screen.getByText(/start your first release/i)).toBeInTheDocument();
    expect(screen.getByTestId("project-new-release-open")).toBeInTheDocument();
  });

  it("marks a folder whose feature needs the user, with the reason in words", () => {
    const stuck = rel({ features: [feat({ id: "f2", key: "division", stageRuns: [run({ status: "BlockedSignoff" })] })] });
    renderHome([stuck]);
    expect(within(screen.getByTestId("project-folder-rel")).getByText(/needs your approval/i)).toBeInTheDocument();
  });

  it("groups shipped releases into a collapsed section that can be opened", () => {
    const shipped = rel({ id: "old", status: "Released", features: [ADDING] });
    renderHome([rel(), shipped]);

    expect(screen.getByTestId("project-shipped-toggle")).toHaveTextContent("Shipped (1)");
    expect(screen.queryByTestId("project-folder-old")).not.toBeInTheDocument();

    fireEvent.click(screen.getByTestId("project-shipped-toggle"));
    expect(screen.getByTestId("project-folder-old")).toHaveAttribute("href", "#/r/old");
  });

  it("does not offer to create twice when the lists are still loading", () => {
    renderHome([rel()], { loading: true });
    expect(screen.getByTestId("project-home-loading")).toBeInTheDocument();
  });

  it("surfaces a plain error with a Try again action", () => {
    const { onRetry } = renderHome([], { error: "We can't reach DevTeam right now. Retrying…" });
    expect(screen.getByRole("alert")).toHaveTextContent(/can't reach DevTeam/i);
    fireEvent.click(screen.getByTestId("project-retry"));
    expect(onRetry).toHaveBeenCalled();
  });

  it("creates a release and hands it to the parent to open", async () => {
    const created = rel({ id: "new", title: "Release negation" });
    mockApi.createReleaseAsync.mockResolvedValue(created);
    const { onReleaseCreated } = renderHome([rel()]);

    fireEvent.click(screen.getByTestId("project-new-release-open"));
    fireEvent.change(screen.getByTestId("project-release-key"), { target: { value: "negation" } });
    fireEvent.click(screen.getByTestId("project-create-release"));

    await waitFor(() => expect(mockApi.createReleaseAsync).toHaveBeenCalledWith("negation", WORKSPACE));
    await waitFor(() => expect(onReleaseCreated).toHaveBeenCalledWith(created));
  });

  it("creates an urgent fix and hands it to the parent to open", async () => {
    mockApi.startHotfixAsync.mockResolvedValue(feat({ id: "hf1", releaseId: "hot" }));
    mockApi.getReleaseAsync.mockResolvedValue(hotfix());
    const { onReleaseCreated } = renderHome([rel()]);

    fireEvent.click(screen.getByTestId("project-new-hotfix-open"));
    fireEvent.change(screen.getByTestId("project-hotfix-key"), { target: { value: "critical-bug" } });
    fireEvent.click(screen.getByTestId("project-start-hotfix"));

    await waitFor(() => expect(mockApi.startHotfixAsync).toHaveBeenCalledWith("critical-bug", WORKSPACE));
    await waitFor(() => expect(onReleaseCreated).toHaveBeenCalledWith(hotfix()));
  });

  it("says so, in plain words, when something fails to create", async () => {
    mockApi.createReleaseAsync.mockRejectedValue(new Error("boom"));
    const { onReleaseCreated } = renderHome([rel()]);

    fireEvent.click(screen.getByTestId("project-new-release-open"));
    fireEvent.change(screen.getByTestId("project-release-key"), { target: { value: "negation" } });
    fireEvent.click(screen.getByTestId("project-create-release"));

    expect(await screen.findByRole("alert")).toHaveTextContent(/couldn't create/i);
    expect(onReleaseCreated).not.toHaveBeenCalled();
  });

  it("never shows git or internal vocabulary", () => {
    renderHome([rel(), hotfix(), rel({ id: "s", status: "Released", features: [ADDING] })]);
    expect(document.body.textContent).not.toMatch(/checkout|stash|branch|HEAD|gate|gherkin|OnHold|BlockedGate|BlockedSignoff|Proposed/i);
  });
});