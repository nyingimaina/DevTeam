import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import GitView from "./GitView";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { GitStatusDto } from "../../Chat/Data/BrokerTypes";

jest.mock("../../Chat/Data/BrokerApi");

jest.mock("./ManageRepositoryPane", () => {
  return {
    __esModule: true,
    default: ({ workspacePath, onClose }: { workspacePath: string; onClose: () => void }) => (
      <div data-testid="manage-repo-pane-mock">
        Manage Repository Mock — {workspacePath}
        <button onClick={onClose}>Close Mock</button>
      </div>
    ),
  };
});

const mockApi = jest.mocked(BrokerApi.prototype);

function makeStatus(overrides: Partial<GitStatusDto> = {}): GitStatusDto {
  return {
    success: true,
    isRepo: true,
    isClean: true,
    branch: "develop",
    branches: ["develop", "main"],
    ahead: 0,
    behind: 0,
    ...overrides,
  };
}

describe("GitView", () => {
  beforeEach(() => {
    jest.clearAllMocks();
    mockApi.getGitStatusAsync.mockResolvedValue(makeStatus());
    mockApi.getGitLogAsync.mockResolvedValue({ success: true, isRepo: true, isClean: true, ahead: 0, behind: 0, commits: [] });
  });

  it("shows the Manage Repository button once status loads", async () => {
    render(<GitView api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);

    await waitFor(() => {
      expect(screen.getByTestId("git-manage-repo-btn")).toBeInTheDocument();
    });
    expect(screen.queryByTestId("manage-repo-pane-mock")).not.toBeInTheDocument();
  });

  it("opens the Manage Repository pane when clicked, and closes it again", async () => {
    const user = userEvent.setup();
    render(<GitView api={mockApi as unknown as BrokerApi} workspacePath={"C:\\work\\proj"} />);

    await waitFor(() => expect(screen.getByTestId("git-manage-repo-btn")).toBeInTheDocument());
    await user.click(screen.getByTestId("git-manage-repo-btn"));

    expect(screen.getByTestId("manage-repo-pane-mock")).toHaveTextContent("C:\\work\\proj");

    await user.click(screen.getByText("Close Mock"));
    expect(screen.queryByTestId("manage-repo-pane-mock")).not.toBeInTheDocument();
  });
});
