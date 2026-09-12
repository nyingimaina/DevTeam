import React from "react";
import { MessageDto, PartDto } from "../Data/BrokerTypes";
import styles from "../Styles/MessageRow.module.css";

export default function MessageRow({ message }: { message: MessageDto }) {
  const isUser = message.role === "user";
  const body = message.bodyText || "";
  const toolParts = message.parts?.filter((p) => p.kind === "tool_call") ?? [];

  return (
    <div className={`${styles.row} ${isUser ? styles.user : styles.assistant}`}>
      <div className={styles.bubble}>
        {body && <div className={styles.body}>{body}</div>}
        {toolParts.map((part) => (
          <ToolCallBody key={part.id} part={part} />
        ))}
      </div>
    </div>
  );
}

function ToolCallBody({ part }: { part: PartDto }) {
  return (
    <div className={styles.toolCall}>
      <span className={styles.toolLabel}>
        {part.toolName ? `tool: ${part.toolName}` : "tool call"}
      </span>
      {part.toolCallId && <span className={styles.toolId}>{part.toolCallId}</span>}
    </div>
  );
}