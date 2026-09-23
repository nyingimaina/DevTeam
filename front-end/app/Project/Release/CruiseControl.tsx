import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { PipelineStageDto, ReleaseDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/Negotiation.module.css";
import { autoAdvanceStopReason, nextAutoStep } from "./autoAdvance";

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
  const [armed, setArmed] = useState(false);
  const [secondsLeft, setSecondsLeft] = useState(AUTO_ADVANCE_SECONDS);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const step = nextAutoStep(release, pipeline);
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

  // Stop (and explain) the moment the next step needs a person.
  useEffect(() => {
    if (armed && step.kind === "wait" && step.reason !== "busy") {
      setArmed(false);
      setMessage(autoAdvanceStopReason(step.reason));
    }
  }, [armed, step]);

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
    if (!armed || busy || step.kind === "wait") {
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
  }, [act, armed, busy, secondsLeft, step.kind]);

  if (readOnly) {
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
            onClick={() => setArmed(false)}
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
            setArmed(true);
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
