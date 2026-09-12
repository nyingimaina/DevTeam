import React from "react";
import { ModelOption, SessionSummary } from "../Data/BrokerTypes";
import styles from "../Styles/ModelSidePane.module.css";

interface IProps {
  models: ModelOption[];
  currentModelId?: string;
  sessions: SessionSummary[];
  activeSessionId?: string;
  onSelectModel: (modelId: string) => void;
  onSelectSession: (sessionId: string) => void;
  onNewSession: () => void;
  onDeleteSession: (sessionId: string) => void;
  onClose: () => void;
  busy?: boolean;
}

export default function ModelSidePane({
  models,
  currentModelId,
  sessions,
  activeSessionId,
  onSelectModel,
  onSelectSession,
  onNewSession,
  onDeleteSession,
  onClose,
  busy,
}: IProps) {
  return (
    <div className={styles.pane}>
      <div className={styles.header}>
        <span className={styles.title}>DevTeam</span>
        <button className={styles.close} onClick={onClose} aria-label="Close side pane">
          ✕
        </button>
      </div>

      <button className={styles.newSession} onClick={onNewSession} disabled={busy}>
        + New conversation
      </button>

      <div className={styles.sectionTitle}>Workspaces</div>
      <ul className={styles.sessionList}>
        {sessions.map((session) => (
          <li key={session.sessionId}>
            <button
              className={`${styles.session} ${session.sessionId === activeSessionId ? styles.active : ""}`}
              onClick={() => onSelectSession(session.sessionId)}
            >
              <span className={styles.sessionName}>{session.title || session.workspacePath}</span>
              <button
                className={styles.delete}
                onClick={(e) => {
                  e.stopPropagation();
                  onDeleteSession(session.sessionId);
                }}
                aria-label="Delete session"
              >
                ·
              </button>
            </button>
          </li>
        ))}
      </ul>

      <div className={styles.sectionTitle}>Model</div>
      {models.length === 0 && <div className={styles.empty}>No models available.</div>}
      <div className={styles.modelList}>
        {models.map((model) => (
          <button
            key={model.value}
            className={`${styles.model} ${model.value === currentModelId ? styles.active : ""}`}
            onClick={() => onSelectModel(model.value)}
          >
            <span className={styles.modelName}>{model.name}</span>
            <span className={styles.modelId}>{model.value}</span>
          </button>
        ))}
      </div>
    </div>
  );
}