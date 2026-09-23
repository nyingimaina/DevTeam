import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import "@testing-library/jest-dom";
import RequirementProgressBars from "./RequirementProgressBars";
import BrokerApi from "../../Chat/Data/BrokerApi";

jest.mock("../../Chat/Data/BrokerApi");

const mockApi = jest.mocked(BrokerApi.prototype);

function renderBars() {
  return render(
    <RequirementProgressBars featureId="f1" api={mockApi as unknown as BrokerApi} testIdPrefix="stage" />,
  );
}

describe("RequirementProgressBars", () => {
  beforeEach(() => jest.clearAllMocks());

  it("renders a code bar and a tests bar once requirements exist", async () => {
    mockApi.getRequirementProgressAsync.mockResolvedValue({
      requirements: 3,
      code: { done: 1, total: 3 },
      tests: { done: 2, total: 3 },
    });

    renderBars();

    expect(await screen.findByTestId("stage-req-progress-code")).toBeInTheDocument();
    expect(screen.getByTestId("stage-req-progress-tests")).toBeInTheDocument();
    expect(screen.getByTestId("stage-req-progress-code")).toHaveAttribute("aria-valuenow", "1");
    expect(screen.getByTestId("stage-req-progress-tests")).toHaveAttribute("aria-valuenow", "2");
  });

  it("renders nothing when there is no requirement list", async () => {
    mockApi.getRequirementProgressAsync.mockResolvedValue({
      requirements: 0,
      code: { done: 0, total: 0 },
      tests: { done: 0, total: 0 },
    });

    const { container } = renderBars();

    await waitFor(() => expect(mockApi.getRequirementProgressAsync).toHaveBeenCalled());
    expect(container.querySelector('[data-testid="stage-req-progress"]')).toBeNull();
  });
});
