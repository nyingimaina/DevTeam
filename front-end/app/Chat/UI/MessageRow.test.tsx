import React from "react";
import { render, screen } from "@testing-library/react";
import MessageRow from "./MessageRow";
import LiveAssistantBubble from "./LiveAssistantBubble";
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
    const text = screen.getByText("hello world");
    expect(text).toBeTruthy();
  });

  it("renders an assistant message with a tool call part", () => {
    const message: MessageDto = {
      id: "2",
      role: "assistant",
      bodyText: "done",
      createdAt: "2026-01-01T00:00:00Z",
      parts: [
        {
          id: "p1",
          kind: "tool_call",
          toolName: "bash",
          toolCallId: "call_1",
          createdAt: "2026-01-01T00:00:00Z",
        },
      ],
    };
    render(<MessageRow message={message} />);
    expect(screen.getByText("done")).toBeTruthy();
    expect(screen.getByText("tool: bash")).toBeTruthy();
  });
});

describe("LiveAssistantBubble", () => {
  it("renders an empty state placeholder", () => {
    render(<LiveAssistantBubble live={{ text: "", toolCalls: [] }} />);
    expect(screen.getByText(/thinking/i)).toBeTruthy();
  });

  it("renders streamed text and tool calls", () => {
    render(
      <LiveAssistantBubble
        live={{ text: "partial", toolCalls: [{ toolCallId: "c1", title: "Run bash" }] }}
      />,
    );
    expect(screen.getByText("partial")).toBeTruthy();
    expect(screen.getByText("Run bash")).toBeTruthy();
  });
});