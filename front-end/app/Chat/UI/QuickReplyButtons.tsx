import React from "react";
import styles from "../Styles/QuickReplyButtons.module.css";

interface IProps {
  options: string[];
  onSelect: (option: string) => void;
  disabled: boolean;
}

export default function QuickReplyButtons({ options, onSelect, disabled }: IProps) {
  return (
    <div className={styles.group} role="group" aria-label="Quick replies">
      {options.map((option) => (
        <button
          key={option}
          type="button"
          className={styles.option}
          disabled={disabled}
          onClick={() => onSelect(option)}
        >
          {option}
        </button>
      ))}
    </div>
  );
}
