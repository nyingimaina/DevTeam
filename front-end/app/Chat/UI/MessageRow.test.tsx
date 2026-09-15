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

  it("strips streamed tool-echo lines from assistant body", () => {
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

  it("renders a tool call chip with friendly label and short id", () => {
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
    expect(screen.getByText("Run command")).toBeInTheDocument();
    expect(screen.getByText("#12345xyz")).toBeInTheDocument();
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
