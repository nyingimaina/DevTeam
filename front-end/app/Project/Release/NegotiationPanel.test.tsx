import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import NegotiationPanel from "./NegotiationPanel";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { NegotiationPointDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

function point(overrides: Partial<NegotiationPointDto> = {}): NegotiationPointDto {
  return {
    id: "p1",
    target: "coverage_matrix",
    summary: "A test must name the requirement it proves",
    expected: "REQ_1_AddCommand_ValidInputs",
    round: 1,
    status: "Open",
    responseKind: "None",
    requirementRef: "REQ-1",
    createdAt: "2026-09-22T00:00:00Z",
    ...overrides,
  };
}

function renderPanel(refreshRelease: () => Promise<unknown> | unknown = () => {}) {
  return render(
    <NegotiationPanel
      featureId="f1"
      api={mockApi as unknown as BrokerApi}
      testIdPrefix="stage"
      refreshRelease={refreshRelease}
    />,
  );
}

describe("NegotiationPanel", () => {
  beforeEach(() => jest.clearAllMocks());

  it("shows nothing when there are no points", async () => {
    mockApi.getNegotiationAsync.mockResolvedValue([]);

    const { container } = renderPanel();

    await waitFor(() => expect(mockApi.getNegotiationAsync).toHaveBeenCalledWith("f1"));
    expect(container.querySelector('[data-testid="stage-negotiation"]')).toBeNull();
  });

  it("renders each open point with what's expected and lets the user close it", async () => {
    mockApi.getNegotiationAsync.mockResolvedValue([point()]);
    mockApi.resolveNegotiationPointAsync.mockResolvedValue(point({ status: "Resolved" }));
    const refresh = jest.fn();

    renderPanel(refresh);

    expect(await screen.findByTestId("stage-negotiation")).toBeInTheDocument();
    expect(screen.getByText("A test must name the requirement it proves")).toBeInTheDocument();
    expect(screen.getByText(/Expected:/)).toBeInTheDocument();
    expect(screen.getByText("REQ-1")).toBeInTheDocument();

    await userEvent.click(screen.getByTestId("stage-negotiation-resolve-p1"));

    await waitFor(() => expect(mockApi.resolveNegotiationPointAsync).toHaveBeenCalledWith("f1", "p1"));
    await waitFor(() => expect(refresh).toHaveBeenCalled());
  });

  it("shows how the stage answered a point", async () => {
    mockApi.getNegotiationAsync.mockResolvedValue([
      point({ responseKind: "Disputed", responseText: "this check looks wrong" }),
    ]);

    renderPanel();

    expect(await screen.findByText(/Disagrees with this point/)).toBeInTheDocument();
    expect(screen.getByText("this check looks wrong")).toBeInTheDocument();
  });
});
