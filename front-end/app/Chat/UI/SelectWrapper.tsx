import React from "react";
import styles from "../Styles/SelectWrapper.module.css";

export interface SelectOption {
  value: string;
  name: string;
  description?: string | null;
}

interface IProps {
  options: SelectOption[];
  value?: string;
  onChange: (value: string) => void;
  label?: string;
  placeholder?: string;
  disabled?: boolean;
  testId?: string;
}

export default function SelectWrapper({
  options,
  value,
  onChange,
  label,
  placeholder,
  disabled,
  testId,
}: IProps) {
  const hasValue = options.some((option) => option.value === value);
  return (
    <div className={styles.wrapper}>
      {label && (
        <label className={styles.label} htmlFor={testId}>
          {label}
        </label>
      )}
      <select
        id={testId}
        className={styles.select}
        value={hasValue ? value : ""}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        aria-label={label ?? placeholder ?? "Select"}
        data-testid={testId}
      >
        {!hasValue && (
          <option value="" disabled>
            {placeholder ?? "Select…"}
          </option>
        )}
        {options.map((option) => (
          <option key={option.value} value={option.value} title={option.description ?? undefined}>
            {option.name}
          </option>
        ))}
      </select>
    </div>
  );
}