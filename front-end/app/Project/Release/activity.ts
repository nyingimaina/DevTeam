import { TurnActivityEntryDto } from "../../Chat/Data/BrokerTypes";

// After this long with no news from the agent, say so — "thinking" and "hung" must not look
// identical. Deliberately generous: a single hard step can legitimately take a while.
export const QUIET_AFTER_MS = 45_000;

const KIND_LABELS: Record<string, string> = {
  tool: "Tool",
  text: "Said",
  thought: "Thinking",
  status: "Status",
};

export function activityKindLabel(kind: string): string {
  return KIND_LABELS[kind] ?? "Activity";
}

// Newest first: a live feed is read from the top.
export function visibleActivity(
  activity: TurnActivityEntryDto[] | null | undefined,
  showThoughts: boolean,
): TurnActivityEntryDto[] {
  if (!activity || activity.length === 0) return [];
  return activity.filter((entry) => showThoughts || entry.kind !== "thought").slice().reverse();
}

export function parseAt(at: string | null | undefined): number | null {
  if (!at) return null;
  const value = Date.parse(at);
  return Number.isNaN(value) ? null : value;
}

export function quietForMs(lastEventAt: string | null | undefined, now: number): number | null {
  const at = parseAt(lastEventAt);
  return at === null ? null : Math.max(0, now - at);
}

export function isQuiet(lastEventAt: string | null | undefined, now: number, thresholdMs = QUIET_AFTER_MS): boolean {
  const quiet = quietForMs(lastEventAt, now);
  return quiet !== null && quiet >= thresholdMs;
}

// Whole-word relative time for a feed row: "just now", "12s ago", "4m ago".
export function feedAge(at: string | null | undefined, now: number): string {
  const value = parseAt(at);
  if (value === null) return "";
  const seconds = Math.max(0, Math.round((now - value) / 1000));
  if (seconds < 3) return "just now";
  if (seconds < 60) return `${seconds}s ago`;
  const minutes = Math.round(seconds / 60);
  return minutes < 60 ? `${minutes}m ago` : `${Math.round(minutes / 60)}h ago`;
}

// The honest quiet-time sentence, in the same plain register as the rest of the screen.
// `hasOutput` distinguishes "went quiet after doing things" (probably thinking) from "has
// produced nothing at all since it started" (very likely wedged) — the old wording said
// "may be working through something hard" for both, which was misleading during a real hang.
export function quietMessage(
  lastEventAt: string | null | undefined,
  now: number,
  hasOutput = true,
): string | null {
  if (!isQuiet(lastEventAt, now)) return null;
  const quiet = quietForMs(lastEventAt, now)!;
  const seconds = Math.round(quiet / 1000);
  const age = seconds < 60 ? `${seconds} seconds` : `${Math.round(seconds / 60)} minute${Math.round(seconds / 60) === 1 ? "" : "s"}`;

  return hasOutput
    ? `the agent has been quiet for ${age} — it may be working through something hard. You can leave it, or cancel and try again.`
    : `the agent hasn't produced anything for ${age} — it looks stuck. Cancel it and run the step again.`;
}
