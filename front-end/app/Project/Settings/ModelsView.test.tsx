import React from "react";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ModelsView from "./ModelsView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ModelCandidateDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");
const mockApi = jest.mocked(BrokerApi.prototype);
const WORKSPACE = "D:\\apps\\calculator";

function candidate(o: Partial<ModelCandidateDto> = {}): ModelCandidateDto {
  return {
    id: "c1",
    modelId: "opencode/big-pickle",
    priority: 0,
    enabled: true,
    userAdded: false,
    cooldownUntil: null,
    lastFailureKind: null,
    lastFailureReason: null,
    cost: 1,
    smartness: 3,
    note: "Free.",
    ...o,
  };
}

const FIRST = candidate();
const SECOND = candidate({
  id: "c2",
  modelId: "opencode-go/kimi-k3",
  priority: 1,
  cost: 3,
  smartness: 5,
  note: "Strong multi-step coding.",
});

function renderView() {
  return render(<ModelsView api={mockApi as unknown as BrokerApi} workspacePath={WORKSPACE} />);
}

beforeEach(() => {
  jest.clearAllMocks();
  mockApi.listModelCandidatesAsync.mockResolvedValue([FIRST, SECOND]);
  mockApi.listAvailableModelsAsync.mockResolvedValue([
    { value: "opencode/big-pickle", name: "Big Pickle" },
    { value: "custom/my-model", name: "My Model" },
  ]);
  mockApi.reorderModelCandidatesAsync.mockImplementation(async (_ws, ids) =>
    ids.map((id, index) => candidate({ id, priority: index })),
  );
  mockApi.setModelCandidateEnabledAsync.mockResolvedValue(undefined);
  mockApi.removeModelCandidateAsync.mockResolvedValue(undefined);
  mockApi.addModelCandidateAsync.mockResolvedValue(candidate({ id: "c3", modelId: "custom/my-model", userAdded: true }));
});

describe("ModelsView", () => {
  it("explains the failover order in plain language", async () => {
    renderView();

    const view = await screen.findByTestId("models-view");
    expect(view).toHaveTextContent(/tried in/i);
    expect(view).toHaveTextContent(/the next one in this list takes over/i);
  });

  it("lists models in priority order with their read-only stats", async () => {
    renderView();

    const list = await screen.findByTestId("models-list");
    const rows = within(list).getAllByRole("listitem");
    expect(rows[0]).toHaveTextContent("#1");
    expect(rows[0]).toHaveTextContent("opencode/big-pickle");
    expect(rows[1]).toHaveTextContent("#2");
    expect(rows[1]).toHaveTextContent("opencode-go/kimi-k3");
    expect(within(list).getByTestId("model-stats-opencode-go/kimi-k3")).toHaveTextContent(/Cost 3\/5 · Capability 5\/5/);
  });

  it("says unknown rather than inventing stats for a model we have no estimate for", async () => {
    mockApi.listModelCandidatesAsync.mockResolvedValue([
      candidate({ id: "c9", modelId: "custom/my-model", userAdded: true, cost: null, smartness: null, note: null }),
    ]);
    renderView();

    expect(await screen.findByTestId("model-stats-custom/my-model")).toHaveTextContent("Stats: unknown");
  });

  it("explains why a model is being skipped, so a slow start makes sense", async () => {
    const until = new Date(Date.now() + 3 * 60_000).toISOString();
    mockApi.listModelCandidatesAsync.mockResolvedValue([
      candidate({ cooldownUntil: until, lastFailureKind: "RateLimited", lastFailureReason: "The AI service is rate-limiting this model." }),
      SECOND,
    ]);
    renderView();

    const cooling = await screen.findByTestId("model-cooling-opencode/big-pickle");
    expect(cooling).toHaveTextContent(/Cooling down until/);
    expect(cooling).toHaveTextContent(/rate-limiting/);
    expect(await screen.findByTestId("models-cooling-notice")).toHaveTextContent(/1 model is cooling down/);
  });

  it("reorders by moving a row and re-saves the whole order", async () => {
    renderView();
    await screen.findByTestId("models-list");

    await userEvent.click(screen.getByTestId("model-down-opencode/big-pickle"));

    await waitFor(() =>
      expect(mockApi.reorderModelCandidatesAsync).toHaveBeenCalledWith(WORKSPACE, ["c2", "c1"]),
    );
  });

  it("can turn a model off without removing it", async () => {
    renderView();
    await screen.findByTestId("models-list");

    await userEvent.click(screen.getByTestId("model-toggle-opencode/big-pickle"));

    expect(mockApi.setModelCandidateEnabledAsync).toHaveBeenCalledWith("c1", false);
  });

  it("can remove a model", async () => {
    renderView();
    await screen.findByTestId("models-list");

    await userEvent.click(screen.getByTestId("model-remove-opencode/big-pickle"));

    expect(mockApi.removeModelCandidateAsync).toHaveBeenCalledWith("c1");
  });

  it("adds a model, offering the ones the agent reports", async () => {
    renderView();
    await screen.findByTestId("models-list");

    await userEvent.type(screen.getByTestId("model-add-input"), "custom/my-model");
    await userEvent.click(screen.getByTestId("model-add-btn"));

    await waitFor(() => expect(mockApi.addModelCandidateAsync).toHaveBeenCalledWith(WORKSPACE, "custom/my-model"));
    // The picker is populated from what the agent actually offers.
    expect(document.querySelectorAll("#available-models option")).toHaveLength(2);
  });

  it("shows a rejected add rather than silently doing nothing", async () => {
    mockApi.addModelCandidateAsync.mockRejectedValue(new Error("opencode/big-pickle is already in the list."));
    renderView();
    await screen.findByTestId("models-list");

    await userEvent.type(screen.getByTestId("model-add-input"), "opencode/big-pickle");
    await userEvent.click(screen.getByTestId("model-add-btn"));

    expect(await screen.findByTestId("models-error")).toHaveTextContent(/already in the list/);
  });
});
