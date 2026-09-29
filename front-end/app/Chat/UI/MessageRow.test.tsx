import React from "react";
import { render, screen, fireEvent } from "@testing-library/react";
import "@testing-library/jest-dom";
import MessageRow from "./MessageRow";
import { MessageDto } from "../Data/BrokerTypes";

describe("MessageRow", () => {
  it("renders a user message bubble", () => {
    const message: MessageDto = {
      id: "1",
      role: "user",
      bodyText: "hello world",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [],
      isPriming: false,
    };
    render(<MessageRow message={message} />);
    expect(screen.getByText("hello world")).toBeTruthy();
  });

  it("renders assistant markdown as rich text", () => {
    const message: MessageDto = {
      id: "2",
      role: "assistant",
      bodyText: "## Plan\n\nFirst do **this**.",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [],
      isPriming: false,
    };
    render(<MessageRow message={message} />);
    expect(screen.getByRole("heading", { level: 2, name: "Plan" })).toBeInTheDocument();
    expect(screen.getByText("this", { selector: "strong" })).toBeInTheDocument();
  });

  it("REQ_2_StreamedToolEchoLines_StayHiddenFromAssistantBody", () => {
    const message: MessageDto = {
      id: "3",
      role: "assistant",
      bodyText: "tool: execute\ncall_abc12345xyz\nHere is the diff.",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [],
      isPriming: false,
    };
    const { container } = render(<MessageRow message={message} />);
    expect(screen.getByText("Here is the diff.")).toBeInTheDocument();
    expect(container).not.toHaveTextContent("tool: execute");
    expect(container).not.toHaveTextContent("call_abc12345xyz");
  });

  it("REQ_1_AssistantMessageWithOneToolCall_RendersNoToolCallNode", () => {
    const message: MessageDto = {
      id: "4",
      role: "assistant",
      bodyText: "done",
      createdAt: "2026-01-01T00:00:00Z",
      isPriming: false,
      parts: [
        {
          id: "p1",
          kind: "tool_call",
          toolName: "bash",
          toolCallId: "call_abc12345xyz",
          createdAt: "2026-01-01T00:00:00Z",
        },
      ],
    };
    render(<MessageRow message={message} />);
    expect(screen.queryByTestId("tool-call")).not.toBeInTheDocument();
    expect(screen.queryByText("Run command")).not.toBeInTheDocument();
    expect(screen.queryByText("#12345xyz")).not.toBeInTheDocument();
    expect(screen.queryByText(/Tool call/)).not.toBeInTheDocument();
    expect(screen.getByText("done")).toBeInTheDocument();
  });

  it("REQ_1_AssistantMessageWithMultipleToolCalls_RendersNoToolCallNodesAtAll", () => {
    const message: MessageDto = {
      id: "4b",
      role: "assistant",
      bodyText: "All finished.",
      createdAt: "2026-01-01T00:00:00Z",
      isPriming: false,
      parts: [
        {
          id: "p1",
          kind: "tool_call",
          toolName: "execute",
          toolCallId: "call_abc12345xyz",
          createdAt: "2026-01-01T00:00:00Z",
        },
        {
          id: "p2",
          kind: "tool_call",
          createdAt: "2026-01-01T00:00:00Z",
        },
        {
          id: "p3",
          kind: "text",
          text: "ignored",
          createdAt: "2026-01-01T00:00:00Z",
        },
      ],
    };
    const { container } = render(<MessageRow message={message} />);
    expect(screen.queryAllByTestId("tool-call")).toHaveLength(0);
    expect(container).not.toHaveTextContent("Tool call");
    expect(container).not.toHaveTextContent("#12345xyz");
    expect(container).not.toHaveTextContent("Run command");
    expect(screen.getByText("All finished.")).toBeInTheDocument();
  });

  it("collapses a framework-injected message to a placeholder instead of the raw text", () => {
    const message: MessageDto = {
      id: "5",
      role: "user",
      bodyText: "You are the business-analyst for feature 'login'. Very long framework prompt text...",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [],
      isPriming: true,
    };
    render(<MessageRow message={message} />);
    expect(screen.getByText("Priming Prompt Injected")).toBeInTheDocument();
    expect(screen.queryByText(/Very long framework prompt text/)).not.toBeInTheDocument();
  });

  it("calls onInspect with the message when the placeholder is clicked", () => {
    const message: MessageDto = {
      id: "6",
      role: "user",
      bodyText: "You are the developer for feature 'login'. Work autonomously...",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [],
      isPriming: true,
    };
    const onInspect = jest.fn();
    render(<MessageRow message={message} onInspect={onInspect} />);

    fireEvent.click(screen.getByText("Priming Prompt Injected"));

    expect(onInspect).toHaveBeenCalledWith(message);
  });

  it("never collapses a genuine, non-priming message even for the assistant role", () => {
    const message: MessageDto = {
      id: "7",
      role: "assistant",
      bodyText: "Here is my plan.",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [],
      isPriming: false,
    };
    render(<MessageRow message={message} />);
    expect(screen.queryByText("Priming Prompt Injected")).not.toBeInTheDocument();
    expect(screen.getByText("Here is my plan.")).toBeInTheDocument();
  });
});
