import React, { useCallback, useEffect, useRef, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/Negotiation.module.css";
import { autoAdvanceStopReason, latestRun, nextAutoStep } from "./autoAdvance";

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
 * "Continue automatically": once the analyst stage is approved, the user can walk away and the
 * remaining autonomous stages run and are approved one after another, with a visible countdown
 * and a Stop button. It stops the moment a human is actually needed (a failed check, or the end
 * of the feature) and says why. The whole decision lives in autoAdvance.ts; this is the shell.
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
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // `release` can still be loading on first mount (the lazy initializer above only sees
  // whatever was passed on that very first render) — pick up the persisted flag the moment
  // real release data arrives, but only once: after that, local Start/Stop clicks (which
  // persist immediately, see setArmedAndPersist) are the source of truth, not each poll's
  // echo of what we already told the server.
  const syncedFromReleaseRef = useRef(release !== undefined);
  useEffect(() => {
    if (!syncedFromReleaseRef.current && release) {
      syncedFromReleaseRef.current = true;
      setArmed(release.autonomousEnabled ?? false);
    }
  }, [release]);

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

  // Stop (and explain) the moment the next step needs a person — persisted, so a reload
  // doesn't silently re-arm into the same blocker.
  useEffect(() => {
    if (armed && step.kind === "wait" && step.reason !== "busy") {
      setArmedAndPersist(false);
      setMessage(autoAdvanceStopReason(step.reason));
    }
  }, [armed, step, setArmedAndPersist]);

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

  if (readOnly) {
    return null;
  }

  // AutoRunNotice already owns this exact moment (see deferToAutoRunNotice above) — showing our
  // own countdown alongside its "starting automatically…" message would be confusing, and racing
  // it entirely would double-trigger runStageAsync.
  if (deferToAutoRunNotice) {
    return null;
  }

  // Nothing worth offering before the conversation stage is done.
  if (!armed && step.kind === "wait" && step.reason === "interactive") {
    return null;
  }

  return (
    <section className={styles.panel} data-testid={`${testIdPrefix}-cruise`}>
      {armed ? (
        <div className={styles.actions}>
          <span className={styles.heading} data-testid={`${testIdPrefix}-cruise-countdown`}>
            {step.kind === "signoff"
              ? `Approving the next step in ${secondsLeft}s`
              : `Starting the next step in ${secondsLeft}s`}
          </span>
          <button
            type="button"
            data-testid={`${testIdPrefix}-cruise-stop`}
            onClick={() => setArmedAndPersist(false)}
          >
            Stop
          </button>
        </div>
      ) : (
        <button
          type="button"
          data-testid={`${testIdPrefix}-cruise-start`}
          onClick={() => {
            setMessage(null);
            setArmedAndPersist(true);
          }}
        >
          Continue automatically
        </button>
      )}
      {message && (
        <div className={styles.meta} data-testid={`${testIdPrefix}-cruise-message`}>
          {message}
        </div>
      )}
    </section>
  );
}
