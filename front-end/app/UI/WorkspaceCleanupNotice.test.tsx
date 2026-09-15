import React from "react";
import { render, screen, fireEvent, act } from "@testing-library/react";
import "@testing-library/jest-dom";
import WorkspaceCleanupNotice from "./WorkspaceCleanupNotice";

describe("WorkspaceCleanupNotice", () => {
  it("renders the given message", () => {
    render(<WorkspaceCleanupNotice message="Stopped 2 processes from tictactoe: node, GamePlay.Api" onDismiss={jest.fn()} />);
    expect(screen.getByText("Stopped 2 processes from tictactoe: node, GamePlay.Api")).toBeInTheDocument();
  });

  it("calls onDismiss when the dismiss button is clicked", () => {
    const onDismiss = jest.fn();
    render(<WorkspaceCleanupNotice message="Stopped 1 process from tictactoe: node" onDismiss={onDismiss} />);
    fireEvent.click(screen.getByRole("button", { name: /dismiss/i }));
    expect(onDismiss).toHaveBeenCalledTimes(1);
  });

  it("auto-dismisses after the timeout", () => {
    jest.useFakeTimers();
    const onDismiss = jest.fn();
    render(<WorkspaceCleanupNotice message="Stopped 1 process from tictactoe: node" onDismiss={onDismiss} />);

    act(() => {
      jest.advanceTimersByTime(5000);
    });

    expect(onDismiss).toHaveBeenCalledTimes(1);
    jest.useRealTimers();
  });
});
