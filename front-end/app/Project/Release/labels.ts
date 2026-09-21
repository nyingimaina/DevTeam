const STAGE_LABELS: Record<string, string> = {
  "business-analyst": "Business Analyst",
  developer: "Developer",
  qa: "QA",
};

// The BA's output is a named document (Business Requirements Specification) that
// downstream stages reference by that name — every other stage's output is just
// "<stage> output" since it has no equivalent standalone document.
const STAGE_OUTPUT_LABELS: Record<string, string> = {
  "business-analyst": "BRS",
};

const PHASE_LABELS: Record<string, string> = {
  GuidedQA: "Chat with agent",
  Producing: "Producing artifacts",
  Gates: "Checking the work",
  Challenge: "Review in progress",
  Signoff: "Waiting for your approval",
};

const STATUS_LABELS: Record<string, string> = {
  InProgress: "In progress",
  BlockedGate: "Needs attention — some checks didn't pass",
  BlockedSignoff: "Waiting for your approval",
  Blocked: "Blocked",
  Ready: "Ready",
  Complete: "Done",
  Escalated: "Agent error — needs retry",
  // Distinct from BlockedGate: this stage's own gates never ran — an EntryGate checking
  // something the *previous* stage produced failed first.
  BlockedEntry: "Blocked before starting — the previous step needs attention",
  // ReleaseFeatureStatus values (feature-list card, not the release/stage header).
  Proposed: "Not started",
  OnHold: "Parked — switch to resume",
};

// Distinct copy per failure kind so "needs retry" doesn't read as one generic error —
// a provider rejection (won't fix itself on retry) reads very differently from a
// disconnected process or a timeout (both plausibly fixed by just trying again).
const ERROR_KIND_LABELS: Record<string, string> = {
  Disconnected: "The agent process disconnected unexpectedly.",
  ProviderRejected: "The agent's model provider rejected the request — check its configuration.",
  TimedOut: "The agent didn't respond in time.",
};

const WHATS_NEXT: Record<string, string> = {
  "business-analyst": "Answer the agent's prompts one at a time. Type DONE when the requirements are settled.",
  developer: "No chat needed — watch the agent work the workspace. It runs the gates itself when done.",
  qa: "Runs automatically. Reviews findings appear here once the QA agent finishes.",
};

export function stageLabel(name: string): string {
  const known = STAGE_LABELS[name];
  if (known) return known;
  return name
    .split("-")
    .filter(Boolean)
    .map((part) => (part.length <= 3 ? part.toUpperCase() : part.charAt(0).toUpperCase() + part.slice(1)))
    .join(" ");
}

export function stageOutputLabel(name: string): string {
  return STAGE_OUTPUT_LABELS[name] ?? `${stageLabel(name)} output`;
}

export function phaseLabel(phase: string): string {
  return PHASE_LABELS[phase] ?? phase;
}

export function statusLabel(status: string): string {
  return STATUS_LABELS[status] ?? status;
}

export function whatsNext(stageName: string): string {
  return WHATS_NEXT[stageName] ?? "";
}

export function errorKindLabel(kind: string): string {
  return ERROR_KIND_LABELS[kind] ?? "";
}

// This button only re-checks the current stage's gates — it never advances the
// pipeline — so its label must not read as a transition (that was the source of
// user confusion: "Move to Developer" implied a move that never happened).
export function moveOnButtonLabel(nextStageName: string | null): string {
  return nextStageName ? `Check readiness for ${stageLabel(nextStageName)}` : "Check readiness to finish";
}

// This is the only button that both approves signoff and advances the pipeline,
// so its label should say so instead of the generic, non-committal "Proceed".
export function proceedButtonLabel(nextStageName: string | null): string {
  return nextStageName ? `Approve & move to ${stageLabel(nextStageName)}` : "Approve & finish release";
}

export interface GateProblemLike {
  gateName: string;
  title: string;
  whatWentWrong: string;
  technicalDetail?: string;
}

export interface RepairResultLike {
  outcome: string;
  autoFixAttempts: number;
  problems: GateProblemLike[];
}

export interface RepairNotice {
  tone: "info" | "error";
  title: string;
  items: string[];
}

// What to tell the user after a check-and-repair run: nothing when it just passed, a quiet
// note when the agent fixed things itself, and a clear list when it's now their call.
export function repairNotice(result: RepairResultLike | null | undefined): RepairNotice | null {
  if (!result) return null;
  if (result.outcome === "NeedsYou") {
    const title = result.autoFixAttempts > 0
      ? `The agent tried ${result.autoFixAttempts} time${result.autoFixAttempts === 1 ? "" : "s"} to fix this on its own, but it still needs you.`
      : "This needs you.";
    return {
      tone: "error",
      title,
      items: result.problems.map((p) => `${p.title} — ${p.whatWentWrong}`),
    };
  }
  if (result.autoFixAttempts > 0) {
    return { tone: "info", title: "The agent found a problem in its work and fixed it automatically.", items: [] };
  }
  return null;
}