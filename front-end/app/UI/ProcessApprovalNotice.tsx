import React, { useState } from "react";
import { PendingStopDto, StoppedProcessDto } from "../Chat/Data/BrokerTypes";
import { projectNameFromPath } from "../Project/workspaceCleanup";
import styles from "./WorkspaceCleanupNotice.module.css";

interface IProcessApprovalNoticeProps {
  workspacePath: string;
  pending: PendingStopDto[];
  onApprove: (processIds: number[]) => Promise<StoppedProcessDto[]>;
  onDismiss: () => void;
}

/**
 * Processes the sweep flagged as foreign in this workspace. DevTeam will not stop a process it
 * cannot attribute to itself — including another OpenCode agent in the same repo — so this is
 * the permission step: plain names, plain reasons, and one explicit click before anything dies.
 */
export default function ProcessApprovalNotice({ workspacePath, pending, onApprove, onDismiss }: IProcessApprovalNoticeProps) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<string | null>(null);

  const stop = async () => {
    setBusy(true);
    setError(null);
    try {
      const stopped = await onApprove(pending.map((item) => item.processId));
      if (stopped.length === 0) {
        setError("The processes could not be stopped (they may have already exited).");
        return;
      }
      const names = stopped.map((item) => item.name).join(", ");
      const noun = stopped.length === 1 ? "process" : "processes";
      setResult(`Stopped ${stopped.length} ${noun}: ${names}`);
    } catch (e) {
      setError(e instanceof Error ? e.message : `${e}`);
    } finally {
      setBusy(false);
    }
  };

  const projectName = projectNameFromPath(workspacePath);

  return (
    <div className={styles.notice} role="alert" data-testid="process-approval-notice">
      <span className={styles.message}>
        {pending.length} process{pending.length === 1 ? "" : "es"} in {projectName} need your approval to stop
      </span>
      <ul>
        {pending.map((item) => (
          <li key={item.processId} data-testid="process-approval-item">
            <div>
              {item.name} (pid {item.processId})
            </div>
            {item.commandLine && <div>{item.commandLine}</div>}
            <div>{item.reason}</div>
          </li>
        ))}
      </ul>
      {error && <div data-testid="process-approval-error">{error}</div>}
      {result && <div data-testid="process-approval-result">{result}</div>}
      <div className={styles.message}>
        <button
          type="button"
          onClick={() => void stop()}
          disabled={busy}
          data-testid="process-approval-stop-btn"
        >
          {busy ? "Stopping…" : "Stop them"}
        </button>{" "}
        <button
          type="button"
          onClick={onDismiss}
          disabled={busy}
          aria-label="Dismiss"
          data-testid="process-approval-dismiss-btn"
        >
          Leave running
        </button>
      </div>
    </div>
  );
}
