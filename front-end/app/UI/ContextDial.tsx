import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import BrokerApi from "../Chat/Data/BrokerApi";
import { contextFraction, type ContextDto } from "../Chat/Data/ContextTypes";
import styles from "./ContextDial.module.css";

/**
 * Occupancy at which the dial turns amber, and where the compact gesture becomes worthwhile.
 *
 * Matches the broker's own auto-compaction threshold, so the dial and the automatic behaviour agree
 * about what "nearly full" means instead of prompting at one number and firing at another.
 */
const COMPACT_FROM = 0.75;

/** Occupancy at which the dial turns red. */
const CRITICAL_AT = 0.9;

/** A compaction that frees less than this share of the window is not worth having done. */
const WORTHWHILE_FREED = 0.10;

const SIZE = 44;
const STROKE = 5;
const RADIUS = (SIZE - STROKE) / 2;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

function formatTokens(value: number): string {
  if (value >= 1_000_000) return `${(value / 1_000_000).toFixed(1)}M`;
  if (value >= 1_000) return `${Math.round(value / 1_000)}k`;
  return String(value);
}

interface Props {
  api: BrokerApi;
  /** How often to re-read, in ms. The agent reports once per turn, so this can be unhurried. */
  pollMs?: number;
}

/**
 * How full the agent's context window is, as a dial with the percentage in the middle.
 *
 * A ring rather than a bar because the number is the point: the share of the window is a single
 * quantity, and a dial puts the digits in the middle of the shape instead of beside it, so the
 * figure and the picture cannot be read as two separate facts.
 *
 * Double click compacts — an unobtrusive gesture for something you rarely do — and it is also
 * reachable with Enter or Space, because a gesture only one input method can perform is not a
 * feature so much as a trick.
 */
export default function ContextDial({ api, pollMs = 10_000 }: Props) {
  const [context, setContext] = useState<ContextDto | null>(null);
  const [outcome, setOutcome] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const clickTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const refresh = useCallback(async () => {
    try {
      setContext(await api.getContextAsync());
    } catch {
      // A failed poll is not worth interrupting anyone for: the last reading stays on the dial and
      // the next tick tries again. Surfacing errors for a background refresh trains people to
      // ignore errors.
    }
  }, [api]);

  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), pollMs);
    return () => clearInterval(timer);
  }, [refresh, pollMs]);

  useEffect(() => () => {
    if (clickTimer.current) clearTimeout(clickTimer.current);
  }, []);

  const compact = useCallback(async () => {
    setBusy(true);
    setError(null);
    setOutcome(null);
    try {
      const result = await api.compactContextAsync();
      setContext(result.context);
      const message = describeCompaction(result.freedTokens, result.usedTokensBefore);
      // The visible outcome carries role="status", so it is announced as well as shown — no second
      // copy of the text is needed for anyone who cannot see it.
      setOutcome(message);
    } catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      setError(message);
    } finally {
      setBusy(false);
    }
  }, [api]);

  // A double click is two clicks, so the first one is held briefly and discarded if a second
  // follows. Without this every compaction would also fire from an accidental single click.
  const onClick = () => {
    if (clickTimer.current) clearTimeout(clickTimer.current);
    clickTimer.current = setTimeout(() => { clickTimer.current = null; }, 250);
  };

  const onDoubleClick = () => {
    if (clickTimer.current) {
      clearTimeout(clickTimer.current);
      clickTimer.current = null;
    }
    if (!busy) void compact();
  };

  // Enter and Space on a button raise a click, not a double click, so without this the keyboard path
  // would only ever arm the single-click timer and never compact. A gesture reachable with a mouse
  // alone is not accessible, so the key is handled directly.
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (e.key !== "Enter" && e.key !== " ") return;
    e.preventDefault();
    if (clickTimer.current) {
      clearTimeout(clickTimer.current);
      clickTimer.current = null;
    }
    if (!busy) void compact();
  };

  const fraction = contextFraction(context);
  const measured = fraction !== null;
  const percent = measured ? Math.round((fraction as number) * 100) : null;
  const dash = (fraction ?? 0) * CIRCUMFERENCE;
  const tone = !measured ? "unknown" : (fraction as number) >= CRITICAL_AT
    ? "critical"
    : (fraction as number) >= COMPACT_FROM ? "warning" : "ok";
  const gradientId = useMemo(
    () => `context-arc-${tone}-${Math.round((fraction ?? 0) * 100)}`,
    [tone, fraction],
  );

  const label = measured
    ? `Context ${percent}% full. ${context?.contextSize ? `${formatTokens(context.usedTokens)} of ${formatTokens(context.contextSize)} tokens.` : ""} Double click or press Enter to compact.`
    : "Context has not been measured yet.";

  return (
    <div className={styles.dialSlot}>
      <div className={styles.dialWrap}>
        <button
          type="button"
          data-testid="context-dial"
          className={`${styles.dial} ${styles[tone]} ${context?.turnActive ? styles.active : ""}`}
          style={{ width: SIZE, height: SIZE }}
          onClick={onClick}
          onDoubleClick={onDoubleClick}
          onKeyDown={onKeyDown}
          disabled={busy || !measured}
          aria-label={label}
          title={label}
        >
          <svg width={SIZE} height={SIZE} viewBox={`0 0 ${SIZE} ${SIZE}`} aria-hidden="true" focusable="false">
            <defs>
              {/* A separate gradient per occupancy so the arc warms up as the context fills, rather
                  than staying one colour and making the dial's colour the only signal. */}
              <linearGradient id={gradientId} x1="0" y1="0" x2="1" y2="1">
                <stop offset="0%" stopColor={ARC_FROM[tone]} />
                <stop offset="100%" stopColor={ARC_TO[tone]} />
              </linearGradient>
            </defs>
            <circle
              className={styles.track}
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              fill="none"
              strokeWidth={STROKE}
            />
            <circle
              data-testid="context-dial-arc"
              data-circumference={CIRCUMFERENCE}
              className={styles.arc}
              cx={SIZE / 2}
              cy={SIZE / 2}
              r={RADIUS}
              fill="none"
              strokeWidth={STROKE}
              stroke={`url(#${gradientId})`}
              strokeLinecap="round"
              strokeDasharray={`${Math.max(0, dash)} ${CIRCUMFERENCE}`}
              // Start the arc at twelve o'clock rather than at three, so a partly-full dial reads
              // as "this much of the way round" instead of appearing to start from the side.
              transform={`rotate(-90 ${SIZE / 2} ${SIZE / 2})`}
            />
          </svg>
          <span className={styles.percent} data-testid="context-dial-percent">
            {percent === null ? "–" : `${percent}%`}
          </span>
        </button>

        {busy && <span className={styles.busy}>Compacting…</span>}
        {context?.turnActive && <span className={styles.stale}>turn in progress</span>}
        {!measured && <span className={styles.unmeasured}>not measured</span>}
        {outcome && <span className={styles.outcome} role="status">{outcome}</span>}
        {error && <span className={styles.error} role="alert">{error}</span>}
      </div>
    </div>
  );
}

const ARC_FROM = {
  ok: "#2f9e44",
  warning: "#e8a33d",
  critical: "#d9483b",
  unknown: "#9aa0a6",
} as const;

const ARC_TO = {
  ok: "#66bb6a",
  warning: "#f2c14e",
  critical: "#ef5350",
  unknown: "#b0b4b8",
} as const;

/**
 * Says what the compaction achieved, including the case where it achieved almost nothing.
 *
 * A context that is mostly irreducible instruction does not shrink much however often it is
 * summarized, and reporting that plainly is more useful than a success message — the answer is a
 * different feature, not another compaction.
 */
function describeCompaction(freedTokens: number, before: number): string {
  if (before <= 0 || freedTokens < before * WORTHWHILE_FREED) {
    return "Barely any room was freed — most of this context is not summarizable.";
  }
  return `Freed ${freedTokens.toLocaleString()} tokens.`;
}
