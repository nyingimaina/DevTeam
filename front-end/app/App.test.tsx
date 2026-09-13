import React from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import App from "./App";

jest.mock("./Chat/State/ChatLogic", () => {
  return {
    __esModule: true,
    default: class MockChatLogic {
      repository = {
        brokerReady: true,
        agentName: "test-agent",
        brokerVersion: "1.0",
        activeSession: null,
        sessions: [],
        currentModelId: undefined,
        currentModeId: undefined,
        busy: false,
        isPending: false,
        error: null,
        liveAssistant: undefined,
      };
      setRerender() {}
      initializeAsync = jest.fn().mockResolvedValue(undefined);
      disposeAsync = jest.fn().mockResolvedValue(undefined);
    },
  };
});

jest.mock("./Chat/Data/BrokerApi", () => {
  return {
    __esModule: true,
    default: jest.fn().mockImplementation(() => ({
      getHealthAsync: jest.fn().mockResolvedValue({ status: "ok", version: "1.0" }),
      getInfoAsync: jest.fn().mockResolvedValue({ agentName: "test", protocolVersion: "1" }),
      listSessionsAsync: jest.fn().mockResolvedValue([]),
      listReleasesAsync: jest.fn().mockResolvedValue([]),
      listFileSystemRootsAsync: jest.fn().mockResolvedValue([]),
    })),
  };
});

jest.mock("./Project/Release/ReleaseWizard", () => {
  return {
    __esModule: true,
    default: ({ testIdPrefix, workspacePath }: { testIdPrefix?: string; workspacePath?: string }) => (
      <div data-testid={testIdPrefix ?? "release-wizard"}>
        ReleaseWizard Mock — {workspacePath}
      </div>
    ),
  };
});

jest.mock("./Chat/UI/Chat", () => {
  return {
    __esModule: true,
    default: ({ workspacePath }: { workspacePath?: string }) => (
      <div data-testid="chat-mock">Chat Mock — {workspacePath}</div>
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

beforeEach(() => {
  localStorage.clear();
});

describe("App", () => {
  it("shows PathBrowser when no project is open", () => {
    render(<App />);
    expect(screen.getByTestId("path-browser")).toBeInTheDocument();
    expect(screen.queryByTestId("release-wizard")).not.toBeInTheDocument();
    expect(screen.queryByTestId("chat-mock")).not.toBeInTheDocument();
  });

  it("opens project when folder is picked", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    expect(screen.getByTestId("release-wizard")).toBeInTheDocument();
    expect(screen.getByText(/ReleaseWizard Mock/)).toHaveTextContent("C:\\work\\my-project");
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
    expect(screen.getByTestId("release-wizard")).toBeInTheDocument();
    expect(screen.getByText(/ReleaseWizard Mock/)).toHaveTextContent("C:\\work\\saved");
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
    expect(screen.getByTestId("release-wizard")).toBeInTheDocument();

    fireEvent.click(screen.getByText("my-project"));
    expect(screen.getByTestId("path-browser")).toBeInTheDocument();
    expect(localStorage.getItem("devteam-project")).toBeNull();
  });

  it("switches to Chat tab", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));
    fireEvent.click(screen.getByText("Chat"));
    expect(screen.getByTestId("chat-mock")).toBeInTheDocument();
    expect(screen.queryByTestId("release-wizard")).not.toBeInTheDocument();
  });

  it("passes workspacePath to both tabs", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Pick"));

    fireEvent.click(screen.getByText("Chat"));
    expect(screen.getByText(/Chat Mock/)).toHaveTextContent("C:\\work\\my-project");

    fireEvent.click(screen.getByText("Releases"));
    expect(screen.getByText(/ReleaseWizard Mock/)).toHaveTextContent("C:\\work\\my-project");
  });
});
