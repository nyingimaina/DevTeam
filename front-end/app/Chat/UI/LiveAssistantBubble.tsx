import React from "react";
import { LiveAssistant } from "../State/ChatRepository";
import RichText from "../../UI/RichText";
import { friendlyToolLabel, shortCallId } from "./MessageRow";
import styles from "../Styles/MessageRow.module.css";

export default function LiveAssistantBubble({ live }: { live: LiveAssistant }) {
  return (
    <div className={`${styles.row} ${styles.assistant}`}>
      <div className={styles.bubble}>
        {live.text && <RichText text={live.text} />}
        {(live.toolCalls ?? []).map((call, index) => (
          <div key={call.toolCallId || index} className={styles.toolCall} data-testid="tool-call">
            <span className={styles.toolLabel}>
              {call.title || friendlyToolLabel(call.kind || "tool")}
            </span>
            {call.toolCallId && <span className={styles.toolId}>{shortCallId(call.toolCallId)}</span>}
          </div>
        ))}
        {!live.text && (live.toolCalls ?? []).length === 0 && (
          <div className={styles.thinking}>thinking…</div>
        )}
      </div>
    </div>
  );
}