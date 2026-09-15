import React from "react";
import { MessageDto, PartDto } from "../Data/BrokerTypes";
import RichText from "../../UI/RichText";
import styles from "../Styles/MessageRow.module.css";

interface IMessageRowProps {
  message: MessageDto;
  onInspect?: (message: MessageDto) => void;
}

export default function MessageRow({ message, onInspect }: IMessageRowProps) {
  const isUser = message.role === "user";
  const toolParts = message.parts?.filter((p) => p.kind === "tool_call") ?? [];

  return (
    <div className={`${styles.row} ${isUser ? styles.user : styles.assistant}`}>
      <div className={styles.bubble}>
        {message.bodyText &&
          (message.isPriming ? (
            <button
              type="button"
              className={styles.primingNotice}
              onClick={() => onInspect?.(message)}
            >
              Priming Prompt Injected
            </button>
          ) : isUser ? (
            <div className={styles.body}>{message.bodyText}</div>
          ) : (
            <RichText text={message.bodyText} />
          ))}
        {toolParts.map((part) => (
          <ToolCallBody key={part.id} part={part} />
        ))}
      </div>
    </div>
  );
}

export function ToolCallBody({ part }: { part: PartDto }) {
  const label = part.toolName ? friendlyToolLabel(part.toolName) : "Tool call";
  const shortId = part.toolCallId ? shortCallId(part.toolCallId) : null;
  return (
    <div className={styles.toolCall} data-testid="tool-call">
      <span className={styles.toolLabel}>{label}</span>
      {shortId && <span className={styles.toolId}>{shortId}</span>}
    </div>
  );
}

export function friendlyToolLabel(name: string): string {
  switch (name) {
    case "bash":
    case "execute":
      return "Run command";
    case "write":
      return "Write file";
    case "read":
      return "Read file";
    default:
      return name.charAt(0).toUpperCase() + name.slice(1);
  }
}

export function shortCallId(id: string): string {
  const clean = id.replace(/^call_/, "");
  return `#${clean.slice(-8)}`;
}