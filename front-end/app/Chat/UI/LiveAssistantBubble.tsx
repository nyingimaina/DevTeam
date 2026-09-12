import React from "react";
import { LiveAssistant } from "../State/ChatRepository";
import styles from "../Styles/MessageRow.module.css";

export default function LiveAssistantBubble({ live }: { live: LiveAssistant }) {
  return (
    <div className={`${styles.row} ${styles.assistant}`}>
      <div className={styles.bubble}>
        {live.text && <div className={styles.body}>{live.text}</div>}
        {live.toolCalls.map((call, index) => (
          <div key={call.toolCallId || index} className={styles.toolCall}>
            <span className={styles.toolLabel}>{call.title || call.kind || "tool"}</span>
          </div>
        ))}
        {!live.text && live.toolCalls.length === 0 && (
          <div className={styles.thinking}>thinking…</div>
        )}
      </div>
    </div>
  );
}