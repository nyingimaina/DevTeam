import React, { useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { RequirementProgressDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/ReleaseWizard.module.css";
import { requirementProgressBars, StepProgress } from "./progress";

interface IRequirementProgressBarsProps {
  featureId: string;
  api: BrokerApi;
  testIdPrefix: string;
  /** Changes as the stage progresses, so the bars refetch instead of showing a stale number. */
  refreshKey?: string | number;
}

/**
 * Two deterministic bars showing how much of the requirement list is implemented (code) and
 * proven (tests). Shown once there is a requirement list; hidden otherwise so an early stage
 * isn't cluttered with 0/0.
 */
export default function RequirementProgressBars({
  featureId,
  api,
  testIdPrefix,
  refreshKey,
}: IRequirementProgressBarsProps) {
  const [progress, setProgress] = useState<RequirementProgressDto | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await api.getRequirementProgressAsync(featureId);
        if (!cancelled) setProgress(result ?? null);
      } catch {
        if (!cancelled) setProgress(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [api, featureId, refreshKey]);

  const bars = requirementProgressBars(progress);
  if (!bars.visible) {
    return null;
  }

  return (
    <div className={styles.requirementProgress} data-testid={`${testIdPrefix}-req-progress`}>
      {/* "In code" is deliberately not "done": it counts requirements this feature's code names,
          which is a reference, not proof the work is finished. */}
      <ProgressRow label="In code" testId={`${testIdPrefix}-req-progress-code`} value={bars.code} />
      <ProgressRow label="In tests" testId={`${testIdPrefix}-req-progress-tests`} value={bars.tests} />
    </div>
  );
}

function ProgressRow({ label, testId, value }: { label: string; testId: string; value: StepProgress }) {
  return (
    <div className={styles.requirementProgressRow}>
      <span className={styles.requirementProgressLabel}>{label}</span>
      <div
        className={styles.stageProgress}
        data-testid={testId}
        role="progressbar"
        aria-valuemin={0}
        aria-valuemax={value.total}
        aria-valuenow={value.completed}
        aria-label={`${label}: ${value.completed} of ${value.total}`}
      >
        <div className={styles.stageProgressFill} style={{ width: `${Math.round(value.ratio * 100)}%` }} />
      </div>
      <span className={styles.requirementProgressCount}>
        {value.completed} of {value.total}
      </span>
    </div>
  );
}
