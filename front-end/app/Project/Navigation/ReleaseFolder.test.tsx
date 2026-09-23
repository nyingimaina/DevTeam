import React from "react";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import "@testing-library/jest-dom";
import ReleaseFolder from "./ReleaseFolder";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);

const NOW = new Date("2026-01-10T12:00:00Z");
const PIPELINE = ["business-analyst", "developer", "qa"];

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
    id: "rel", workspacePath: "D:\\apps\\calculator", title: "Release adding", version: "0.1.0", status: "Ready", branchName: "release/adding",
    currentFeatureId: "f1", createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-10T10:00:00Z",
    features: [ADDING, feat()], stageRuns: [], signoffs: [], flowPosition: null, ...o,
  };
}

function renderFolder(release: ReleaseDto, extra: Partial<React.ComponentProps<typeof ReleaseFolder>> = {}) {
  const onReleaseUpdated = jest.fn();
  const onFeatureCreated = jest.fn();
  render(
    <ReleaseFolder
      api={mockApi as unknown as BrokerApi}
      release={release}
      pipelineStageNames={PIPELINE}
      activeFeature={null}
      onReleaseUpdated={onReleaseUpdated}
      onFeatureCreated={onFeatureCreated}
      now={NOW}
      {...extra}
    />,
  );
  return { onReleaseUpdated, onFeatureCreated };
}

describe("ReleaseFolder", () => {
  beforeEach(() => jest.clearAllMocks());

  it("lists every feature — done and in progress — as a card that links to its workflow", () => {
    renderFolder(rel());

    const sub = screen.getByTestId("release-feature-card-subtraction");
    const add = screen.getByTestId("release-feature-card-adding");
    expect(sub).toHaveAttribute("href", "#/r/rel/f/f1");
    expect(add).toHaveAttribute("href", "#/r/rel/f/f0");
    expect(within(add).getByText("Done")).toBeInTheDocument();
    expect(within(sub).getByText("In progress")).toBeInTheDocument();
  });

  it("shows where an in-progress feature is, and when it last moved", () => {
    renderFolder(rel());

    const sub = screen.getByTestId("release-feature-card-subtraction");
    expect(within(sub).getByText("Developer — stage 2 of 3")).toBeInTheDocument();
    expect(within(sub).getByText("2 hours ago")).toBeInTheDocument();
  });

  it("reports how many features are done", () => {
    renderFolder(rel());
    expect(screen.getByTestId("release-folder-summary")).toHaveTextContent("1 of 2 features done");
  });

  it("puts a feature that needs the user first, with the reason in words", () => {
    const stuck = feat({ id: "f2", key: "division", stageRuns: [run({ status: "BlockedSignoff" })], updatedAt: "2026-01-01T00:00:00Z" });
    renderFolder(rel({ features: [ADDING, feat(), stuck] }));

    const cards = screen.getAllByTestId(/^release-feature-card-/);
    expect(cards[0]).toHaveAttribute("data-testid", "release-feature-card-division");
    expect(within(cards[0]).getByText(/Needs your approval/)).toBeInTheDocument();
  });

  it("always offers a new feature — even when everything is done", () => {
    renderFolder(rel({ features: [ADDING], currentFeatureId: null }));
    expect(screen.getByTestId("release-new-feature-open")).toBeInTheDocument();
  });

  it("offers to ship only when every feature is done (the reported bug: stale 'Ready')", () => {
    renderFolder(rel()); // stored Ready, but subtraction is unfinished
    expect(screen.queryByTestId("release-ship-release-btn")).not.toBeInTheDocument();
    expect(screen.getByTestId("release-folder-state")).toHaveTextContent("In progress");
  });

  it("offers to ship — and says so — when every feature really is done", () => {
    renderFolder(rel({ features: [ADDING], currentFeatureId: null }));
    expect(screen.getByTestId("release-ship-release-btn")).toBeInTheDocument();
    expect(screen.getByTestId("release-folder-state")).toHaveTextContent("Ready to ship");
  });

  it("has a friendly empty state that still lets you start", () => {
    renderFolder(rel({ features: [], currentFeatureId: null, status: "InProgress" }));
    expect(screen.getByText(/No features yet/i)).toBeInTheDocument();
    expect(screen.getByTestId("release-new-feature-open")).toBeInTheDocument();
  });

  it("marks the feature being worked on right now", () => {
    renderFolder(rel());
    expect(within(screen.getByTestId("release-feature-card-subtraction")).getByText("Active")).toBeInTheDocument();
    expect(within(screen.getByTestId("release-feature-card-adding")).queryByText("Active")).not.toBeInTheDocument();
  });

  it("names a paused feature as paused", () => {
    renderFolder(rel({ features: [ADDING, feat({ id: "f5", key: "multiplication", status: "OnHold" })], currentFeatureId: null }));
    expect(within(screen.getByTestId("release-feature-card-multiplication")).getByText("Paused")).toBeInTheDocument();
  });

  it("never shows git or internal vocabulary", () => {
    renderFolder(rel({ features: [ADDING, feat(), feat({ id: "f5", key: "m", status: "OnHold" }), feat({ id: "f6", key: "d", stageRuns: [run({ status: "BlockedGate" })] })] }));
    expect(document.body.textContent).not.toMatch(/checkout|stash|branch|HEAD|gherkin|OnHold|BlockedGate|BlockedSignoff|Proposed/i);
  });

  describe("starting a new feature", () => {
    it("creates it and reports where to go", async () => {
      mockApi.createFeatureAsync.mockResolvedValue(feat({ id: "new1", key: "division" }));
      const fresh = rel({ features: [ADDING, feat(), feat({ id: "new1", key: "division" })] });
      mockApi.getReleaseAsync.mockResolvedValue(fresh);
      const { onReleaseUpdated, onFeatureCreated } = renderFolder(rel());

      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.change(screen.getByTestId("release-new-feature-key"), { target: { value: "division" } });
      fireEvent.click(screen.getByTestId("release-new-feature-create"));

      await waitFor(() => expect(mockApi.createFeatureAsync).toHaveBeenCalledWith("rel", "division"));
      await waitFor(() => expect(onFeatureCreated).toHaveBeenCalledWith("new1"));
      expect(onReleaseUpdated).toHaveBeenCalledWith(fresh);
    });

    it("warns which feature will be paused before creating anything", async () => {
      renderFolder(rel(), { activeFeature: { key: "multiplication" } });

      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.change(screen.getByTestId("release-new-feature-key"), { target: { value: "division" } });
      fireEvent.click(screen.getByTestId("release-new-feature-create"));

      const warning = await screen.findByTestId("release-new-feature-warning");
      expect(warning).toHaveTextContent("division");
      expect(warning).toHaveTextContent("multiplication");
      expect(warning).toHaveTextContent(/paused/i);
      expect(mockApi.createFeatureAsync).not.toHaveBeenCalled();
    });

    it("creates once the pause is confirmed", async () => {
      mockApi.createFeatureAsync.mockResolvedValue(feat({ id: "new1", key: "division" }));
      mockApi.getReleaseAsync.mockResolvedValue(rel());
      const { onFeatureCreated } = renderFolder(rel(), { activeFeature: { key: "multiplication" } });

      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.change(screen.getByTestId("release-new-feature-key"), { target: { value: "division" } });
      fireEvent.click(screen.getByTestId("release-new-feature-create"));
      fireEvent.click(await screen.findByTestId("release-new-feature-confirm"));

      await waitFor(() => expect(mockApi.createFeatureAsync).toHaveBeenCalledWith("rel", "division"));
      await waitFor(() => expect(onFeatureCreated).toHaveBeenCalledWith("new1"));
    });

    it("does not create with a blank name", () => {
      renderFolder(rel());
      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.change(screen.getByTestId("release-new-feature-key"), { target: { value: "   " } });

      expect(screen.getByTestId("release-new-feature-create")).toBeDisabled();
    });

    it("refuses a name this release already has, in plain words", () => {
      renderFolder(rel());
      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.change(screen.getByTestId("release-new-feature-key"), { target: { value: " Subtraction " } });
      fireEvent.click(screen.getByTestId("release-new-feature-create"));

      expect(screen.getByRole("alert")).toHaveTextContent(/already a feature called/i);
      expect(mockApi.createFeatureAsync).not.toHaveBeenCalled();
    });

    it("says so when creating fails, and creates nothing further", async () => {
      mockApi.createFeatureAsync.mockRejectedValue(new Error("boom"));
      const { onFeatureCreated } = renderFolder(rel());

      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.change(screen.getByTestId("release-new-feature-key"), { target: { value: "division" } });
      fireEvent.click(screen.getByTestId("release-new-feature-create"));

      expect(await screen.findByRole("alert")).toHaveTextContent(/couldn't create that feature/i);
      expect(onFeatureCreated).not.toHaveBeenCalled();
    });

    it("can be cancelled", () => {
      renderFolder(rel());
      fireEvent.click(screen.getByTestId("release-new-feature-open"));
      fireEvent.click(screen.getByTestId("release-new-feature-cancel"));

      expect(screen.queryByTestId("release-new-feature-key")).not.toBeInTheDocument();
      expect(screen.getByTestId("release-new-feature-open")).toBeInTheDocument();
    });
  });
});
