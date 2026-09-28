import React from "react";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import "@testing-library/jest-dom";
import ProcessApprovalNotice from "./ProcessApprovalNotice";
import { PendingStopDto } from "../Chat/Data/BrokerTypes";

const PENDING: PendingStopDto[] = [
  {
    processId: 5001,
    name: "OpenCode.exe",
    executablePath: "D:\\apps\\calc\\.tools\\OpenCode.exe",
    commandLine: null,
    reason: "An OpenCode process DevTeam did not start (pid 5001).",
  },
  {
    processId: 5002,
    name: "node.exe",
    executablePath: "C:\\nvm4w\\nodejs\\node.exe",
    commandLine: "node D:\\apps\\calc\\front-end\\next dev",
    reason: "A workspace process DevTeam did not start — stopped only with your approval.",
  },
];

describe("ProcessApprovalNotice", () => {
  it("lists every process awaiting approval with its plain-word reason", () => {
    render(
      <ProcessApprovalNotice
        workspacePath="D:\\apps\\calc"
        pending={PENDING}
        onApprove={jest.fn().mockResolvedValue(undefined)}
        onDismiss={jest.fn()}
      />,
    );

    expect(screen.getByTestId("process-approval-notice")).toBeInTheDocument();
    expect(screen.getAllByTestId("process-approval-item")).toHaveLength(2);
    expect(screen.getByText(/OpenCode\.exe/)).toBeInTheDocument();
    expect(screen.getByText(/did not start \(pid 5001\)/i)).toBeInTheDocument();
    expect(screen.getByText(/next dev/)).toBeInTheDocument();
  });

  it("stops only on the user's explicit action and reports what was stopped", async () => {
    const onApprove = jest.fn().mockResolvedValue([{ processId: 5001, name: "OpenCode.exe" }]);
    render(
      <ProcessApprovalNotice
        workspacePath="D:\\apps\\calc"
        pending={PENDING}
        onApprove={onApprove}
        onDismiss={jest.fn()}
      />,
    );

    await userEvent.click(screen.getByTestId("process-approval-stop-btn"));

    await waitFor(() => expect(onApprove).toHaveBeenCalledWith([5001, 5002]));
    const result = await screen.findByTestId("process-approval-result");
    expect(result).toHaveTextContent(/Stopped 1 process: OpenCode\.exe/i);
  });

  it("stays honest when the stop fails and keeps the list for a retry", async () => {
    const onApprove = jest.fn().mockRejectedValue(new Error("the broker could not reach the process"));
    render(
      <ProcessApprovalNotice
        workspacePath="D:\\apps\\calc"
        pending={PENDING}
        onApprove={onApprove}
        onDismiss={jest.fn()}
      />,
    );

    await userEvent.click(screen.getByTestId("process-approval-stop-btn"));

    expect(await screen.findByTestId("process-approval-error")).toHaveTextContent(/could not reach the process/i);
    // Still on screen: the user decides again rather than the list vanishing.
    expect(screen.getByTestId("process-approval-notice")).toBeInTheDocument();
  });

  it("dismisses without asking anything", async () => {
    const onDismiss = jest.fn();
    render(
      <ProcessApprovalNotice
        workspacePath="D:\\apps\\calc"
        pending={PENDING}
        onApprove={jest.fn()}
        onDismiss={onDismiss}
      />,
    );

    await userEvent.click(screen.getByTestId("process-approval-dismiss-btn"));
    expect(onDismiss).toHaveBeenCalled();
  });
});
