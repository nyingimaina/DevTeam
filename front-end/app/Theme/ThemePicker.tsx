"use client";
import React from "react";
import { ThemeMode } from "./ThemeProvider";
import styles from "./ThemePicker.module.css";

interface IProps {
  mode: ThemeMode;
  onChange: (mode: ThemeMode) => void;
}

const OPTIONS: { value: ThemeMode; label: string }[] = [
  { value: "light", label: "Light" },
  { value: "system", label: "System" },
  { value: "dark", label: "Dark" },
];

export default function ThemePicker({ mode, onChange }: IProps) {
  return (
    <div className={styles.group} role="group" aria-label="Theme">
      {OPTIONS.map((option) => (
        <button
          key={option.value}
          type="button"
          className={`${styles.option} ${mode === option.value ? styles.active : ""}`}
          aria-pressed={mode === option.value}
          onClick={() => onChange(option.value)}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}