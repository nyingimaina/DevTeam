"use client";
import React, { useRef, useState } from "react";
import ZestButton from "jattac.libs.web.zest-button";
import { PipelineEditorRoleDto } from "../../../Chat/Data/BrokerTypes";
import styles from "../Styles/PlaceholderAutocompleteInput.module.css";

// Mirrors the backend's ArtifactRoots.All — kept here (not re-derived from PipelineView's
// labeled picker options) so this module has no dependency on any one consumer's UI.
export const ARTIFACT_ROOT_KEYS = [
  "docs-root",
  "feature-docs-root",
  "feature-code-root-back",
  "feature-code-root-front",
  "workspace-root",
];

// The full set of known placeholder tokens a prompt author can reference — <F>, every named
// root, and one <stage-name/artifact.file> entry per role that declares an Artifact. Recomputed
// live from whatever pipeline the caller has fetched, so a newly-declared artifact stage shows
// up immediately without any extra wiring.
export function placeholderTokens(roles: PipelineEditorRoleDto[]): string[] {
  return [
    "<F>",
    ...ARTIFACT_ROOT_KEYS.map((key) => `<${key}>`),
    ...roles.filter((r) => r.artifact != null).map((r) => `<${r.name}/artifact.file>`),
  ];
}

interface IAutocompleteContext {
  start: number;
  end: number;
  query: string;
}

export interface IPlaceholderAutocompleteInputProps {
  value: string;
  onChange: (value: string) => void;
  tokens: string[];
  placeholder?: string;
  testId: string;
  /** Renders a <textarea> instead of a single-line <input> — for longer prose (e.g. stage
   * instructions, profile prompts) where a one-line field isn't a good fit. */
  multiline?: boolean;
}

/**
 * A small, self-contained trigger-character ("<") autocomplete for any prompt-authoring
 * field that references DevTeam's known placeholder tokens (<F>, named roots,
 * <stage/artifact.file>), plus an explicit "Insert placeholder" button so a novice who
 * doesn't already know about the "<" convention can still discover and use it by browsing.
 *
 * Deliberately not a pulled-in library: the actual need — a closed set of ~10 known tokens —
 * is narrow, and the one candidate library that fit (react-mentions) was npm-deprecated with
 * an unpatched vulnerability.
 */
export default function PlaceholderAutocompleteInput({
  value, onChange, tokens, placeholder, testId, multiline = false,
}: IPlaceholderAutocompleteInputProps) {
  const inputRef = useRef<HTMLInputElement | HTMLTextAreaElement>(null);
  const [context, setContext] = useState<IAutocompleteContext | null>(null);
  const [highlightIndex, setHighlightIndex] = useState(0);

  const detectTypedTrigger = (text: string, cursor: number): IAutocompleteContext | null => {
    const uptoCursor = text.slice(0, cursor);
    const openIndex = uptoCursor.lastIndexOf("<");
    if (openIndex === -1) return null;
    const between = uptoCursor.slice(openIndex + 1);
    if (between.includes(">") || /\s/.test(between)) return null;
    return { start: openIndex, end: cursor, query: between };
  };

  const matches = context ? tokens.filter((t) => t.toLowerCase().includes(context.query.toLowerCase())) : [];

  const applySuggestion = (token: string) => {
    if (!context) return;
    const before = value.slice(0, context.start);
    const after = value.slice(context.end);
    onChange(`${before}${token}${after}`);
    setContext(null);
    requestAnimationFrame(() => {
      const pos = before.length + token.length;
      inputRef.current?.focus();
      inputRef.current?.setSelectionRange(pos, pos);
    });
  };

  const openBrowseAll = () => {
    const cursor = inputRef.current?.selectionStart ?? value.length;
    setContext({ start: cursor, end: cursor, query: "" });
    setHighlightIndex(0);
    inputRef.current?.focus();
  };

  const onChangeHandler = (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    onChange(e.target.value);
    setContext(detectTypedTrigger(e.target.value, e.target.selectionStart ?? e.target.value.length));
    setHighlightIndex(0);
  };

  const onKeyDownHandler = (e: React.KeyboardEvent<HTMLInputElement | HTMLTextAreaElement>) => {
    if (!context || matches.length === 0) return;
    if (e.key === "ArrowDown") { e.preventDefault(); setHighlightIndex((i) => (i + 1) % matches.length); }
    else if (e.key === "ArrowUp") { e.preventDefault(); setHighlightIndex((i) => (i - 1 + matches.length) % matches.length); }
    else if (e.key === "Enter" || e.key === "Tab") {
      // A multiline field needs plain Enter to insert a real newline — only intercept it
      // while a suggestion is actually open.
      e.preventDefault();
      applySuggestion(matches[highlightIndex]);
    } else if (e.key === "Escape") { setContext(null); }
  };

  // Deferred so a suggestion's onMouseDown (which calls preventDefault) still fires before
  // the list is torn down by this blur.
  const onBlurHandler = () => setTimeout(() => setContext(null), 150);

  return (
    <div className={styles.autocompleteGroup}>
      <div className={styles.autocompleteWrapper}>
        {multiline ? (
          <textarea
            ref={inputRef as React.RefObject<HTMLTextAreaElement>}
            className={styles.autocompleteTextarea}
            data-testid={testId}
            value={value}
            placeholder={placeholder}
            rows={4}
            onChange={onChangeHandler}
            onKeyDown={onKeyDownHandler}
            onBlur={onBlurHandler}
          />
        ) : (
          <input
            ref={inputRef as React.RefObject<HTMLInputElement>}
            type="text"
            className={styles.autocompleteInput}
            data-testid={testId}
            value={value}
            placeholder={placeholder}
            onChange={onChangeHandler}
            onKeyDown={onKeyDownHandler}
            onBlur={onBlurHandler}
          />
        )}
        {context && matches.length > 0 && (
          <ul className={styles.autocompleteList} data-testid={`${testId}-suggestions`}>
            {matches.map((token, i) => (
              <li
                key={token}
                className={i === highlightIndex ? styles.autocompleteOptionActive : styles.autocompleteOption}
                onMouseDown={(e) => { e.preventDefault(); applySuggestion(token); }}
              >
                {token}
              </li>
            ))}
          </ul>
        )}
      </div>
      <ZestButton
        type="button"
        onClick={openBrowseAll}
        data-testid={`${testId}-browse-btn`}
        zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
      >
        Insert placeholder
      </ZestButton>
    </div>
  );
}
