import React from "react";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "./App";

const cleanupWorkspaceAsync = jest.fn().mockResolvedValue({ stopped: [], requiresApproval: [] });
const approveProcessStopAsync = jest.fn().mockResolvedValue([]);

jest.mock("./Chat/Data/BrokerApi", () => {
  return {
    __esModule: true,
    default: jest.fn().mockImplementation(() => ({
      getHealthAsync: jest.fn().mockResolvedValue({ status: "ok", version: "1.0" }),
      getInfoAsync: jest.fn().mockResolvedValue({ agentName: "test", protocolVersion: "1" }),
      listReleasesAsync: jest.fn().mockResolvedValue([]),
      listFileSystemRootsAsync: jest.fn().mockResolvedValue([]),
      cleanupWorkspaceAsync,
      approveProcessStopAsync,
      getCurrentTurnAsync: jest.fn().mockResolvedValue(undefined),
      cancelCurrentTurnAsync: jest.fn().mockResolvedValue(true),
    })),
  };
});

// `userAction` keeps the clicks readable next to fireEvent (same library underneath).
const userAction = userEvent;

jest.mock("./Project/Navigation/ProjectNavigator", () => {
  function ProjectNavigatorMock({ workspacePath }: { workspacePath?: string }) {
    const [count, setCount] = React.useState(0);
    return (
      <div data-testid="project-navigator">
        ProjectNavigator Mock — {workspacePath}
        <button onClick={() => setCount((c) => c + 1)}>Increment Release Count</button>
        <span data-testid="release-count">{count}</span>
      </div>
    );
  }
  return {
    __esModule: true,
    default: ProjectNavigatorMock,
  };
});

jest.mock("./Project/Git/GitView", () => {
  return {
    __esModule: true,
    default: ({ workspacePath }: { workspacePath?: string }) => (
      <div data-testid="git-mock">Git Mock — {workspacePath}</div>
    ),
  };
});

jest.mock("./Project/UI/PathBrowser", () => {
  return {
    __esModule: true,
    default: ({ onSelect }: { onSelect?: (path: string) => void }) => (
      <div data-testid="path-browser">
        <button onClick={() => onSelect?.("C:\\work\\my-project")}>Pick</button>
      </div>
    ),
  };
});

jest.mock("./Project/Settings/SettingsView", () => {
  return {
    __esModule: true,
    default: ({ workspacePath }: { workspacePath?: string }) => (
      <div data-testid="settings-mock">Settings Mock — {workspacePath}</div>
    ),
  };
});

interface MockSidekickMenuItem {
  label: React.ReactNode;
  onClick?: () => void;
}

jest.mock("jattac.libs.web.zest-sidekick-menu", () => {
  return {
    __esModule: true,
    default: ({ items }: { items: MockSidekickMenuItem[] }) => (
      <nav data-testid="sidekick-menu-mock">
        {items.map((item, i) => (
          <button key={i} onClick={item.onClick}>
            {item.label}
          </button>
        ))}
      </nav>
    ),
  };
});

beforeEach(() => {
  localStorage.clear();
  cleanupWorkspaceAsync.mockReset();
  cleanupWorkspaceAsync.mockResolvedValue({ stopped: [], requiresApproval: [] });
  approveProcessStopAsync.mockReset();
  approveProcessStopAsync.mockResolvedValue([]);
});

describe("App", () => {
  it("shows PathBrowser when no project is open", () => {
    render(<App />);
    expect(screen.getByTestId("path-browser")).toBeInTheDocument();
    expect(screen.queryByTestId("project-navigator")).not.toBeInTheDocument();
  });

  it("opens project when folder is picked", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    expect(screen.getByTestId("project-navigator")).toBeInTheDocument();
    expect(screen.getByText(/ProjectNavigator Mock/)).toHaveTextContent("C:\\work\\my-project");
    expect(screen.queryByTestId("path-browser")).not.toBeInTheDocument();
  });

  it("persists project in localStorage", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    expect(localStorage.getItem("devteam-project")).toBe("C:\\work\\my-project");
  });

  it("restores project from localStorage", () => {
    localStorage.setItem("devteam-project", "C:\\work\\saved");
    render(<App />);
    expect(screen.getByTestId("project-navigator")).toBeInTheDocument();
    expect(screen.getByText(/ProjectNavigator Mock/)).toHaveTextContent("C:\\work\\saved");
    expect(screen.queryByTestId("path-browser")).not.toBeInTheDocument();
  });

  it("shows folder name in tab bar", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    expect(screen.getByText("my-project")).toBeInTheDocument();
  });

  it("clicking folder name clears project", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    expect(screen.getByTestId("project-navigator")).toBeInTheDocument();

    fireEvent.click(screen.getByText("my-project"));
    expect(screen.getByTestId("path-browser")).toBeInTheDocument();
    expect(localStorage.getItem("devteam-project")).toBeNull();
  });

  it("does not show a Chat tab", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    expect(screen.queryByText("Chat")).not.toBeInTheDocument();
  });

  it("passes workspacePath to both tabs", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    fireEvent.click(screen.getByText("Git"));
    expect(screen.getByText(/Git Mock/)).toHaveTextContent("C:\\work\\my-project");

    fireEvent.click(screen.getByText("Releases"));
    expect(screen.getByText(/ProjectNavigator Mock/)).toHaveTextContent("C:\\work\\my-project");
  });

  it("keeps a previously opened tab's state alive when switching away and back", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    fireEvent.click(screen.getByText("Increment Release Count"));
    expect(screen.getByTestId("release-count")).toHaveTextContent("1");

    fireEvent.click(screen.getByText("Git"));
    expect(screen.queryByTestId("project-navigator")).not.toBeVisible();

    fireEvent.click(screen.getByText("Releases"));
    expect(screen.getByTestId("release-count")).toHaveTextContent("1");
  });

  it("switches to Git tab", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    fireEvent.click(screen.getByText("Git"));
    expect(screen.getByTestId("git-mock")).toBeVisible();
    expect(screen.queryByTestId("project-navigator")).not.toBeVisible();
  });

  it("sweeps the newly opened path for stray processes", async () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    await waitFor(() => {
      expect(cleanupWorkspaceAsync).toHaveBeenCalledWith("C:\\work\\my-project");
    });
  });

  it("sweeps the path being left when closing a project", async () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    await waitFor(() => expect(cleanupWorkspaceAsync).toHaveBeenCalledTimes(1));
    cleanupWorkspaceAsync.mockClear();

    fireEvent.click(screen.getByText("my-project"));

    await waitFor(() => {
      expect(cleanupWorkspaceAsync).toHaveBeenCalledWith("C:\\work\\my-project");
    });
  });

it("shows a notice listing stopped processes after opening a project", async () => {
  cleanupWorkspaceAsync.mockResolvedValue({
    stopped: [
      { processId: 1, name: "node.exe" },
      { processId: 2, name: "GamePlay.Api.exe" },
    ],
    requiresApproval: [],
  });
  render(<App />);

  fireEvent.click(screen.getByText("Pick"));

  await waitFor(() => {
    expect(screen.getByText("Stopped 2 processes from my-project: node.exe, GamePlay.Api.exe")).toBeInTheDocument();
  });
});

it("asks for approval instead of stopping processes it does not own", async () => {
  cleanupWorkspaceAsync.mockResolvedValue({
    stopped: [],
    requiresApproval: [
      {
        processId: 501,
        name: "OpenCode.exe",
        executablePath: "C:\\tools\\OpenCode.exe",
        commandLine: null,
        reason: "An OpenCode process DevTeam did not start (pid 501).",
      },
    ],
  });
  render(<App />);

  fireEvent.click(screen.getByText("Pick"));

  const notice = await screen.findByTestId("process-approval-notice");
  expect(notice).toBeInTheDocument();
  expect(screen.getByText(/did not start \(pid 501\)/i)).toBeInTheDocument();
  expect(screen.getByTestId("process-approval-stop-btn")).toBeInTheDocument();

  // Killing is never automatic: the confirm click is what stops it.
  expect(approveProcessStopAsync).not.toHaveBeenCalled();
});

it("sends exactly the listed pids when the user approves the stop", async () => {
  approveProcessStopAsync.mockResolvedValue([{ processId: 501, name: "OpenCode.exe" }]);
  cleanupWorkspaceAsync.mockResolvedValue({
    stopped: [],
    requiresApproval: [
      {
        processId: 501,
        name: "OpenCode.exe",
        executablePath: "C:\\tools\\OpenCode.exe",
        commandLine: null,
        reason: "An OpenCode process DevTeam did not start (pid 501).",
      },
    ],
  });
  render(<App />);

  fireEvent.click(screen.getByText("Pick"));
  fireEvent.click(await screen.findByTestId("process-approval-stop-btn"));
  await waitFor(() => expect(approveProcessStopAsync).toHaveBeenCalledWith([501]));

  expect(
    await screen.findByTestId("process-approval-result"),
  ).toHaveTextContent(/Stopped 1 process: OpenCode\.exe/i);
});

  it("shows no notice when the sweep finds nothing", async () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    await waitFor(() => expect(cleanupWorkspaceAsync).toHaveBeenCalled());
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("switches to Settings via the sidekick menu", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    fireEvent.click(screen.getByText("Settings"));
    expect(screen.getByTestId("settings-mock")).toBeVisible();
    expect(screen.queryByTestId("project-navigator")).not.toBeVisible();
    expect(screen.getByText(/Settings Mock/)).toHaveTextContent("C:\\work\\my-project");
  });

  it("navigates via the sidekick menu instead of a tab bar", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    expect(screen.getByTestId("sidekick-menu-mock")).toBeInTheDocument();
  });

  it("still opens the project normally when the cleanup sweep fails", async () => {
    cleanupWorkspaceAsync.mockRejectedValue(new Error("boom"));
    render(<App />);

    fireEvent.click(screen.getByText("Pick"));

    await waitFor(() => {
      expect(screen.getByTestId("project-navigator")).toBeInTheDocument();
    });
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });
});
