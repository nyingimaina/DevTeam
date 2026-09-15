import React from "react";
import { render, screen } from "@testing-library/react";
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
});