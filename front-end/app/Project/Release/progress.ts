import { ReleaseDto, ReleaseStageRunDto, RequirementProgressDto } from "../../Chat/Data/BrokerTypes";

export interface StepProgress {
  completed: number;
  total: number;
  ratio: number;
}

// How far the step checklist has got. A step counts as reached once a check row exists for it
// (passed or failed) — the same rule StepChecklist ticks on, so the bar and the list agree.
export function stepProgress(steps: string[], gateChecks: { name: string }[] | undefined): StepProgress {
  const seen = new Set((gateChecks ?? []).map((gc) => gc.name));
  const completed = steps.filter((s) => seen.has(s)).length;
  const total = steps.length;
  return { completed, total, ratio: total === 0 ? 0 : completed / total };
}

export interface RequirementProgressBars {
  visible: boolean;
  code: StepProgress;
  tests: StepProgress;
}

// How much of the requirement list is done, as two bars. Deterministic: a requirement counts for
// "tests" when a test names it and for "code" when a source file is tagged with it (the backend
// computes both). Hidden until a requirement list exists.
export function requirementProgressBars(
  progress: RequirementProgressDto | null | undefined,
): RequirementProgressBars {
  const total = progress?.requirements ?? 0;
  const toStep = (count: { done: number } | undefined): StepProgress => {
    const completed = count?.done ?? 0;
    return { completed, total, ratio: total === 0 ? 0 : completed / total };
  };

  return { visible: total > 0, code: toStep(progress?.code), tests: toStep(progress?.tests) };
}

// Durations of this stage's *finished* runs, oldest first — the sample an estimate learns from.
export function historicalStageDurationsMs(release: ReleaseDto, stageName: string): number[] {
  const runs: ReleaseStageRunDto[] = [
    ...(release.stageRuns ?? []),
    ...(release.features ?? []).flatMap((f) => f.stageRuns ?? []),
  ];

  const durations: number[] = [];
  for (const run of runs) {
    if (run.stageName !== stageName || !run.finishedAt || !run.startedAt) continue;
    const ms = Date.parse(run.finishedAt) - Date.parse(run.startedAt);
    if (Number.isFinite(ms) && ms > 0) durations.push(ms);
  }

  return durations.sort((a, b) => a - b);
}

// Exponential moving average: recent runs predict the next one better than a flat mean, and it
// needs no history at all to stay silent. Null until there is something to learn from.
export function estimateStageDurationMs(durationsMs: number[], alpha = 0.4): number | null {
  if (durationsMs.length === 0) return null;

  let estimate = durationsMs[0];
  for (let i = 1; i < durationsMs.length; i += 1) {
    estimate = alpha * durationsMs[i] + (1 - alpha) * estimate;
  }
  return Math.round(estimate);
}

// "about 15 minutes" / "about a minute" — never a false-precision countdown.
export function formatEstimate(ms: number): string {
  if (ms < 45_000) return "under a minute";
  const minutes = Math.round(ms / 60_000);
  return minutes <= 1 ? "about a minute" : `about ${minutes} minutes`;
}

// Running-late wording once the current attempt has clearly outlasted its own history.
export function overdueNote(elapsedMs: number, estimateMs: number | null): string | null {
  if (estimateMs === null || elapsedMs < estimateMs * 1.5) return null;
  return `this is taking longer than usual — it normally takes ${formatEstimate(estimateMs)}`;
}
