import { PipelineStageDto, ReleaseDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

/**
 * What "continue automatically" should do next, given the release as it currently stands. Pure
 * and synchronous so the cruise-control UI is a thin shell over one testable decision, and so the
 * rule "stop the moment a human is actually needed" lives in exactly one place.
 */
export type AutoStep =
  | { kind: "run-stage" }
  | { kind: "signoff"; stageName: string }
  | { kind: "wait"; reason: "interactive" | "blocked" | "busy" | "done" | "not-ready" };

// A run in one of these states is still working — leave it alone.
const BUSY = new Set(["Active", "GatesRunning", "Producing", "GuidedQA", "Challenge", "Signoff"]);
// A human has to look at these; auto-advance must stop, not retry.
const NEEDS_HUMAN = new Set(["BlockedGate", "BlockedEntry", "Escalated"]);
const DONE_STATUSES = new Set(["Ready", "Complete", "Shipped"]);

export function latestRun(runs: ReleaseStageRunDto[] | undefined, stageName: string): ReleaseStageRunDto | undefined {
  return (runs ?? [])
    .filter((run) => run.stageName === stageName)
    .sort((a, b) => (b.attempt ?? 0) - (a.attempt ?? 0))[0];
}

export function nextAutoStep(release: ReleaseDto | undefined, pipeline: PipelineStageDto[]): AutoStep {
  if (!release) {
    return { kind: "wait", reason: "not-ready" };
  }

  if (DONE_STATUSES.has(release.effectiveStatus ?? release.status)) {
    return { kind: "wait", reason: "done" };
  }

  const stageName = release.flowPosition?.currentStageName;
  if (!stageName || stageName === "done") {
    return { kind: "wait", reason: "done" };
  }

  const role = pipeline.find((stage) => stage.name === stageName);
  const run = latestRun(release.stageRuns, stageName);
  const pending = (release.signoffs ?? []).some(
    (signoff) => signoff.stageName === stageName && signoff.required && !signoff.approved,
  );

  // A stage that finished and is only waiting for approval can be approved for the user.
  if (pending && run?.status === "BlockedSignoff") {
    return { kind: "signoff", stageName };
  }

  if (run && NEEDS_HUMAN.has(run.status)) {
    return { kind: "wait", reason: "blocked" };
  }

  if (run && BUSY.has(run.status)) {
    return { kind: "wait", reason: "busy" };
  }

  // Nothing running for this stage yet: run it — unless it's the one stage that talks to the
  // human, in which case stop and wait for them.
  if (role?.userInputRequired) {
    return { kind: "wait", reason: "interactive" };
  }

  return { kind: "run-stage" };
}

/** The one plain sentence a user sees for a reason auto-advance stopped. */
export function autoAdvanceStopReason(reason: "interactive" | "blocked" | "busy" | "done" | "not-ready"): string {
  switch (reason) {
    case "interactive":
      return "Waiting for you to finish the first conversation.";
    case "blocked":
      return "Paused — something needs your attention.";
    case "done":
      return "All stages are done. Review the feature and finish it when you're happy.";
    case "not-ready":
      return "Nothing to continue yet.";
    default:
      return "Still working — continuing shortly.";
  }
}
