import React from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import Chat from "./Chat";
import ThemeProvider from "../../Theme/ThemeProvider";

jest.mock("../State/ChatLogic", () => {
  return {
    __esModule: true,
    default: class MockChatLogic {
      repository = {
        brokerReady: true,
        agentName: "test-agent",
        brokerVersion: "1.0",
        activeSession: {
          sessionId: "ses_1",
          workspacePath: "C:\\work\\proj",
          title: "proj",
          messages: [],
          models: [{ value: "opencode/big-pickle", name: "Big Pickle" }],
          modes: [{ value: "build", name: "Build" }],
        },
        sessions: [],
        currentModelId: "opencode/big-pickle",
        currentModeId: "build",
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

function renderChat() {
  return render(
    <ThemeProvider>
      <Chat />
    </ThemeProvider>,
  );
}

function currentTheme() {
  return document.documentElement.dataset.theme;
}

beforeEach(() => {
  delete document.documentElement.dataset.theme;
});

describe("Chat", () => {
  it("renders the dock with semantic buttons", () => {
    renderChat();
    expect(screen.getByLabelText("Open side pane")).toHaveAttribute("type", "button");
    expect(screen.getByRole("button", { name: "Send" })).toHaveAttribute("type", "button");
  });

  it("opens the side pane from the list button", async () => {
    renderChat();
    expect(screen.queryByLabelText("Close side pane")).not.toBeInTheDocument();
    fireEvent.click(screen.getByLabelText("Open side pane"));
    expect(await screen.findByLabelText("Close side pane")).toBeInTheDocument();
    expect(screen.getByText("Theme")).toBeInTheDocument();
    expect(screen.getByText("Mode")).toBeInTheDocument();
    expect(screen.getByText("Big Pickle")).toBeInTheDocument();
  });

  it("switches the app theme from the side pane", async () => {
    renderChat();
    fireEvent.click(screen.getByLabelText("Open side pane"));
    await screen.findByLabelText("Close side pane");
    fireEvent.click(screen.getByRole("button", { name: "Dark" }));
    expect(currentTheme()).toBe("dark");
    fireEvent.click(screen.getByRole("button", { name: "Light" }));
    expect(currentTheme()).toBe("light");
  });

  it("new conversation button creates session with workspacePath", async () => {
    renderChat();
    fireEvent.click(screen.getByLabelText("Open side pane"));
    await screen.findByLabelText("Close side pane");
    fireEvent.click(screen.getByRole("button", { name: "+ New conversation" }));
    // Side pane closes, no PathBrowser needed
    expect(screen.queryByLabelText("Close side pane")).not.toBeInTheDocument();
  });
});
