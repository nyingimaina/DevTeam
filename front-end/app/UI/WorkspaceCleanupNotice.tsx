import React, { useEffect } from "react";
import { FaXmark } from "react-icons/fa6";
import styles from "./WorkspaceCleanupNotice.module.css";

const AUTO_DISMISS_MS = 5000;

interface IWorkspaceCleanupNoticeProps {
  message: string;
  onDismiss: () => void;
}

export default function WorkspaceCleanupNotice({ message, onDismiss }: IWorkspaceCleanupNoticeProps) {
  useEffect(() => {
    const timer = window.setTimeout(onDismiss, AUTO_DISMISS_MS);
    return () => window.clearTimeout(timer);
  }, [onDismiss]);

  return (
    <div className={styles.notice} role="status">
      <span className={styles.message}>{message}</span>
      <button type="button" className={styles.dismiss} onClick={onDismiss} aria-label="Dismiss">
        <FaXmark size={12} />
      </button>
    </div>
  );
}
