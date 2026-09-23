import React from "react";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import ProjectNavigator from "./ProjectNavigator";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const PIPELINE: PipelineStageDto[] = [
  { name: "business-analyst", userInputRequired: true, signoff: "requirements-approval", expectedArtifacts: [], steps: ["scaffold_specs", "agent:business-analyst"] },
  { name: "developer", userInputRequired: false, signoff: null, expectedArtifacts: [], steps: ["agent:developer", "verify_code"] },
  { name: "qa", userInputRequired: false, signoff: "release-approval", expectedArtifacts: [], steps: ["agent:qa"] },
];

function run(o: Partial<ReleaseStageRunDto> = {}): ReleaseStageRunDto {
  return {
    id: "sr", releaseFeatureId: "f", stageName: "business-analyst", status: "Active", phase: "GuidedQA", questionCount: 1,
    attempt: 1, startedAt: "2026-01-01T00:00:00Z", readyToProceed: false, gateChecks: [], findings: [], guidanceNotes: [],
    specialistConsultations: [], lastErrorKind: "None", acpSessionId: "acp", ...o,
  };
}

function feature(o: Partial<ReleaseFeatureDto> = {}): ReleaseFeatureDto {
  return {
    id: "f1", releaseId: "rel", key: "subtraction", title: "subtraction", branchName: "feature/subtraction", status: "InProgress",
    createdAt: "2026-01-02T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z", stageRuns: [run({ releaseFeatureId: "f1" })], signoffs: [],
    flowPosition: { id: "fp1", releaseFeatureId: "f1", currentStageIndex: 0, currentStageName: "business-analyst" }, ...o,
  };
}

const ADDING = feature({
  id: "f0", key: "adding", title: "adding", status: "Complete",
  stageRuns: [
    run({ id: "a-ba", releaseFeatureId: "f0", stageName: "business-analyst", status: "Complete", phase: "Signoff" }),
    run({ id: "a-dev", releaseFeatureId: "f0", stageName: "developer", status: "Complete", phase: "Gates", acpSessionId: undefined }),
    run({ id: "a-qa", releaseFeatureId: "f0", stageName: "qa", status: "Complete", phase: "Signoff", acpSessionId: undefined }),
  ],
  flowPosition: { id: "fp0", releaseFeatureId: "f0", currentStageIndex: 3, currentStageName: "done" },
});

function release(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "rel", workspacePath: "D:\\apps\\calculator", title: "Release adding", version: "0.1.0", status: "Ready", branchName: "release/adding",
    currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
    features: [ADDING, feature()], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

function hotfix(o: Partial<ReleaseDto> = {}): ReleaseDto {
  return {
    id: "hot", workspacePath: "D:\\apps\\calculator", title: "Urgent fix div-by-zero", version: "0.1.0", status: "InProgress",
    branchName: "main", isHotfix: true, currentFeatureId: "hf1", createdAt: "2026-01-03T00:00:00Z", updatedAt: "2026-01-03T00:00:00Z",
    features: [feature({ id: "hf1", key: "div-by-zero", releaseId: "hot" })], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

const STATE_CHANGING = [
  "startStageAsync", "runStageAsync", "sendStageMessageAsync", "runStageGatesAsync", "runStageGatesWithRepairAsync",
  "switchStageModelAsync", "setModelAsync", "pushBackAsync", "signoffFeatureAsync", "switchFeatureAsync", "createFeatureAsync",
];

function expectNothingChanged() {
  for (const name of STATE_CHANGING) {
    expect((mockApi as unknown as Record<string, jest.Mock>)[name]).not.toHaveBeenCalled();
  }
}

function renderNavigator() {
  return render(<ProjectNavigator api={mockApi as unknown as BrokerApi} workspacePath="D:\\apps\\calculator" />);
}

describe("ProjectNavigator", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    window.location.hash = "#/";
    mockApi.listReleasesAsync.mockResolvedValue([release()]);
    mockApi.listHotfixesAsync.mockResolvedValue([]);
    mockApi.getPipelineAsync.mockResolvedValue(PIPELINE);
    mockApi.getReleaseAsync.mockResolvedValue(release());
    mockApi.getStageMessagesAsync.mockResolvedValue([
      { id: "m1", role: "user", bodyText: "Add subtraction please", createdAt: "2026-01-02T00:00:00Z", parts: [], isPriming: false },
    ]);
    mockApi.getStageArtifactsAsync.mockResolvedValue([]);
    mockApi.getWorkspaceChangesAsync.mockResolvedValue([]);
    mockApi.getAvailableModelsAsync.mockResolvedValue([]);
    mockApi.getCurrentTurnAsync.mockResolvedValue(undefined);
    mockApi.getSessionAsync.mockResolvedValue({ modelId: "a/one" } as never);
  });

  afterEach(() => {
    window.location.hash = "";
  });

  it("shows the project home for an empty address", async () => {
    renderNavigator();
    expect(await screen.findByTestId("project-home")).toBeInTheDocument();
    expect(screen.getByLabelText("Breadcrumb")).toBeInTheDocument();
  });

  it("opens a release folder from a folder click and updates the address", async () => {
    renderNavigator();

    fireEvent.click(await screen.findByTestId("project-folder-rel"));

    await waitFor(() => expect(window.location.hash).toBe("#/r/rel"));
    expect(await screen.findByTestId("release-folder-summary")).toBeInTheDocument();
    expect(window.location.hash).toBe("#/r/rel");
  });

  it("opens a feature's workflow from a feature card", async () => {
    renderNavigator();

    fireEvent.click(await screen.findByTestId("project-folder-rel"));
    fireEvent.click(await screen.findByTestId("release-feature-card-subtraction"));

    await waitFor(() => expect(window.location.hash).toBe("#/r/rel/f/f1"));
    expect(await screen.findByTestId("release-chat-input")).toBeInTheDocument();
    expectNothingChanged();
  });

  it("restores a deep link on refresh, for a done feature too", async () => {
    window.location.hash = "#/r/rel/f/f0";
    renderNavigator();

    const banner = await screen.findByTestId("release-feature-banner");
    expect(banner).toHaveTextContent(/done/i);
    expect(await screen.findByTestId("release-stage-log")).toBeInTheDocument();
    expectNothingChanged();
  });

  it("keeps the back button working across levels", async () => {
    renderNavigator();

    fireEvent.click(await screen.findByTestId("project-folder-rel"));
    await screen.findByTestId("release-folder-summary");

    fireEvent.click(screen.getByTestId("release-feature-card-subtraction"));
    await screen.findByTestId("release-chat-input");

    window.history.back();
    await waitFor(() => expect(screen.getByTestId("release-folder-summary")).toBeInTheDocument());
    expect(window.location.hash).toBe("#/r/rel");

    window.history.back();
    await waitFor(() => expect(screen.getByTestId("project-home")).toBeInTheDocument());
    expect(window.location.hash).toBe("#/");
  });

  it("opens a hotfix straight into its single feature, folder still in the breadcrumb", async () => {
    mockApi.listReleasesAsync.mockResolvedValue([]);
    mockApi.listHotfixesAsync.mockResolvedValue([hotfix()]);
    window.location.hash = "#/r/hot";
    renderNavigator();

    expect(await screen.findByTestId("release-chat-input")).toBeInTheDocument();
    expect(screen.queryByTestId("release-folder-summary")).not.toBeInTheDocument();
    expect(screen.getByLabelText("Breadcrumb")).toHaveTextContent(/div-by-zero/i);
    expectNothingChanged();
  });

  it("shows a plain not-found with a way home for an unknown release", async () => {
    window.location.hash = "#/r/gone";
    mockApi.getReleaseAsync.mockRejectedValue(new Error("NotFound"));
    renderNavigator();

    expect(await screen.findByTestId("project-not-found")).toHaveTextContent(/couldn't find that release/i);
    expect(screen.getByRole("link", { name: /back to the project/i })).toHaveAttribute("href", "#/");
  });

  it("never shows git or internal vocabulary anywhere it renders", async () => {
    renderNavigator();
    fireEvent.click(await screen.findByTestId("project-folder-rel"));
    await screen.findByTestId("release-folder-summary");

    expect(document.body.textContent).not.toMatch(/checkout|stash|branch|HEAD|gate|gherkin|OnHold|BlockedGate|BlockedSignoff|Proposed/i);
  });
});