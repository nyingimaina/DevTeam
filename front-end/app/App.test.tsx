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
    default: ({ testIdPrefix }: { testIdPrefix?: string }) => (
      <div data-testid={testIdPrefix ?? "release-wizard"}>ReleaseWizard Mock</div>
    ),
  };
});

jest.mock("./Chat/UI/Chat", () => {
  return {
    __esModule: true,
    default: () => <div data-testid="chat-mock">Chat Mock</div>,
  };
});

describe("App", () => {
  it("renders with Releases tab active by default", () => {
    render(<App />);
    expect(screen.getByTestId("release-wizard")).toBeInTheDocument();
    expect(screen.queryByTestId("chat-mock")).not.toBeInTheDocument();
  });

  it("switches to Chat tab when clicked", () => {
    render(<App />);
    const chatTab = screen.getByText("Chat");
    fireEvent.click(chatTab);
    expect(screen.getByTestId("chat-mock")).toBeInTheDocument();
    expect(screen.queryByTestId("release-wizard")).not.toBeInTheDocument();
  });

  it("switches back to Releases tab", () => {
    render(<App />);
    fireEvent.click(screen.getByText("Chat"));
    expect(screen.getByTestId("chat-mock")).toBeInTheDocument();

    fireEvent.click(screen.getByText("Releases"));
    expect(screen.getByTestId("release-wizard")).toBeInTheDocument();
    expect(screen.queryByTestId("chat-mock")).not.toBeInTheDocument();
  });
});
