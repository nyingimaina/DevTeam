import React from "react";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import styles from "./RichText.module.css";

const ECHO_LINE_PATTERN = /^(?:tool:\s*[a-zA-Z_]+|call_[a-zA-Z0-9]{6,})\s*$/;

export function cleanAssistantBody(text: string | null | undefined): string {
  if (!text) return "";
  const kept = text
    .split("\n")
    .filter((raw) => !ECHO_LINE_PATTERN.test(raw.trim()))
    .join("\n");
  return kept.trimEnd();
}

export default function RichText({ text }: { text: string | null | undefined }) {
  const clean = cleanAssistantBody(text);
  if (!clean) return null;
  return (
    <div className={styles.root} data-testid="rich-text">
      <ReactMarkdown remarkPlugins={[remarkGfm]}>{clean}</ReactMarkdown>
    </div>
  );
}