import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/Negotiation.module.css";
import { AutoStep, latestRun, nextAutoStep } from "./autoAdvance";

export const AUTO_ADVANCE_SECONDS = 15;

interface ICruiseControlProps {
  featureId: string;
  release: ReleaseDto | undefined;
  pipeline: PipelineStageDto[];
  api: BrokerApi;
  testIdPrefix: string;
  /** Refetch the release after each automatic action. */
  refreshRelease: () => Promise<unknown> | unknown;
  readOnly?: boolean;
}

/**
 * "Continue automatically": a standing, persisted release preference ("keep this running
 * unattended whenever possible"), not a one-shot action — so it's a single always-visible
 * toggle, settable at any time (mid-conversation, mid-run, while blocked), never something you
 * have to stop first to change. Turning it on doesn't require anything else to be true; turning
 * it off doesn't happen on its own just because a blocker showed up — a block is where the
 * automation is paused, not a reason to abandon the preference, so it resumes on its own once
 * the block clears. The status line below the toggle is purely informational. The step decision
 * itself lives in autoAdvance.ts; this is the shell.
 */
export default function CruiseControl({
  featureId,
  release,
  pipeline,
  api,
  testIdPrefix,
  refreshRelease,
  readOnly,
}: ICruiseControlProps) {
  const [armed, setArmed] = useState(() => release?.autonomousEnabled ?? false);
  const [secondsLeft, setSecondsLeft] = useState(AUTO_ADVANCE_SECONDS);
  const [busy, setBusy] = useState(false);

  // The server's own value is authoritative and nothing here ever silently overrides it (no
  // auto-disarm-on-blocker — see the doc comment above), so it's always safe to mirror it as it
  // changes, not just once on mount.
  useEffect(() => {
    setArmed(release?.autonomousEnabled ?? false);
  }, [release?.autonomousEnabled]);

  const setArmedAndPersist = useCallback(
    (next: boolean) => {
      setArmed(next);
      if (release?.id) {
        void Promise.resolve(api.setReleaseAutonomousEnabledAsync(release.id, next)).catch(() => undefined);
      }
    },
    [api, release?.id],
  );

  const step = nextAutoStep(release, pipeline);
  // A fresh non-interactive stage (no run yet) is AutoRunNotice's own moment — it fires
  // immediately on mount, unconditionally, regardless of this being armed. Letting our own
  // countdown also race toward the same runStageAsync call double-triggers it; defer entirely
  // until AutoRunNotice has already started the run (at which point `step.kind` becomes "wait"
  // with reason "busy" and the countdown below no longer applies anyway).
  const stageName = release?.flowPosition?.currentStageName;
  const currentStageRun = stageName ? latestRun(release?.stageRuns, stageName) : undefined;
  // Mirrors ReleaseWizard's own needsFreshStart exactly (modulo the readOnly clause, which
  // CruiseControl already handles via its own separate early return below) — a Complete run
  // still counts as "fresh" here because a stage that just finished starts its next attempt
  // exactly like a brand-new one.
  const deferToAutoRunNotice = step.kind === "run-stage" && (!currentStageRun || currentStageRun.status === "Complete");
  const stepKey =
    step.kind === "signoff"
      ? `signoff:${step.stageName}`
      : step.kind === "wait"
        ? `wait:${step.reason}`
        : step.kind;

  // Each new actionable step gets a fresh countdown.
  useEffect(() => {
    setSecondsLeft(AUTO_ADVANCE_SECONDS);
  }, [stepKey]);

  const act = useCallback(async () => {
    if (step.kind === "run-stage") {
      await api.runStageAsync(featureId);
    } else if (step.kind === "signoff") {
      await api.signoffFeatureAsync(
        featureId,
        step.stageName,
        "automatic",
        "Approved automatically while you were away.",
      );
    }
    await refreshRelease();
  }, [api, featureId, refreshRelease, step]);

  useEffect(() => {
    if (!armed || busy || step.kind === "wait" || deferToAutoRunNotice) {
      return;
    }

    if (secondsLeft <= 0) {
      setBusy(true);
      void act()
        .catch(() => undefined)
        .finally(() => {
          setBusy(false);
          // Give the next round its own window; in the app the poll will already have moved
          // the step on, but this keeps the timer honest if nothing changed yet.
          setSecondsLeft(AUTO_ADVANCE_SECONDS);
        });
      return;
    }

    const timer = window.setTimeout(() => setSecondsLeft((value) => value - 1), 1000);
    return () => window.clearTimeout(timer);
  }, [act, armed, busy, secondsLeft, step.kind, deferToAutoRunNotice]);

  if (readOnly || !release) {
    return null;
  }

  const message = statusMessage(step, deferToAutoRunNotice, busy, secondsLeft);

  return (
    <section className={styles.panel} data-testid={`${testIdPrefix}-cruise`}>
      <label className={styles.toggle}>
        <input
          type="checkbox"
          checked={armed}
          onChange={(e) => setArmedAndPersist(e.target.checked)}
          data-testid={`${testIdPrefix}-cruise-toggle`}
        />
        <span>Continue automatically</span>
      </label>
      {armed && message && (
        <div className={styles.meta} data-testid={`${testIdPrefix}-cruise-message`}>
          {message}
        </div>
      )}
    </section>
  );
}

function statusMessage(
  step: AutoStep,
  deferToAutoRunNotice: boolean,
  busy: boolean,
  secondsLeft: number,
): string | null {
  // AutoRunNotice already shows its own "starting automatically…" message for this moment.
  if (deferToAutoRunNotice) return null;

  if (step.kind === "run-stage") return busy ? "Starting the next step…" : `Starting the next step in ${secondsLeft}s`;
  if (step.kind === "signoff") return busy ? "Approving…" : `Approving the next step in ${secondsLeft}s`;

  switch (step.reason) {
    case "interactive":
      return "Will continue on its own once you finish this conversation.";
    case "blocked":
      return "Paused — something needs your attention. Will continue once it's resolved.";
    case "busy":
      return "Working…";
    case "done":
      return "All stages are done.";
    default:
      return null;
  }
}
