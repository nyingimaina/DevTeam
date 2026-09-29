import React from "react";
import { MessageDto } from "../Data/BrokerTypes";
import RichText from "../../UI/RichText";
import styles from "../Styles/MessageRow.module.css";

interface IMessageRowProps {
  message: MessageDto;
  onInspect?: (message: MessageDto) => void;
}

export default function MessageRow({ message, onInspect }: IMessageRowProps) {
  const isUser = message.role === "user";

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
            // REQ-1: assistant bubbles render only body text; tool_call parts are not rendered.
            // REQ-2: RichText.cleanAssistantBody keeps streamed tool-echo lines hidden.
            <RichText text={message.bodyText} />
          ))}
      </div>
    </div>
  );
}
