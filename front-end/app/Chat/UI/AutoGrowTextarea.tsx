import React, { useCallback, useEffect, useRef, useState } from "react";
import styles from "../Styles/AutoGrowTextarea.module.css";

interface IProps {
  value: string;
  onChange: (value: string) => void;
  onEnter: () => void;
  placeholder?: string;
  disabled?: boolean;
  maxHeight?: number;
}

export default function AutoGrowTextarea({
  value,
  onChange,
  onEnter,
  placeholder,
  disabled,
  maxHeight = 160,
}: IProps) {
  const ref = useRef<HTMLTextAreaElement>(null);
  const [height, setHeight] = useState(40);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    el.style.height = "0px";
    const next = Math.min(Math.max(el.scrollHeight, 40), maxHeight);
    el.style.height = `${next}px`;
    setHeight(next);
  }, [value, maxHeight]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
        e.preventDefault();
        onEnter();
      }
    },
    [onEnter],
  );

  return (
    <textarea
      ref={ref}
      className={styles.input}
      value={value}
      style={{ height }}
      placeholder={placeholder}
      disabled={disabled}
      rows={1}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={handleKeyDown}
    />
  );
}