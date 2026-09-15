const STAGE_LABELS: Record<string, string> = {
  "business-analyst": "Business Analyst",
  developer: "Developer",
  qa: "QA",
};

const PHASE_LABELS: Record<string, string> = {
  GuidedQA: "Chat with agent",
  Producing: "Producing artifacts",
  Gates: "Running gates",
  Challenge: "Review in progress",
  Signoff: "Awaiting signoff",
};

const STATUS_LABELS: Record<string, string> = {
  InProgress: "In progress",
  BlockedGate: "Blocked — gates failing",
  BlockedSignoff: "Awaiting your signoff",
  Blocked: "Blocked",
  Ready: "Ready",
  Complete: "Done",
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

export function phaseLabel(phase: string): string {
  return PHASE_LABELS[phase] ?? phase;
}

export function statusLabel(status: string): string {
  return STATUS_LABELS[status] ?? status;
}

export function whatsNext(stageName: string): string {
  return WHATS_NEXT[stageName] ?? "";
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