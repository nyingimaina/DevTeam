import React, { useCallback, useEffect, useMemo, useState } from "react";
import ZestButton from "jattac.libs.web.zest-button";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { usePoller } from "../../Chat/Data/usePoller";
import { PipelineStageDto, ReleaseDto } from "../../Chat/Data/BrokerTypes";
import {
  PipelineStepper,
  StageCompleteCard,
  StageHistoryCard,
  StageScreen,
  latestRunFor,
  toErrorMessage,
  useApproveSignoff,
} from "../Release/ReleaseWizard";
import { proceedButtonLabel } from "../Release/labels";
import { releaseForFeature } from "./releaseView";
import { buildRoute } from "./routes";
import releaseStyles from "../Styles/ReleaseWizard.module.css";
import styles from "../Styles/Navigation.module.css";

interface IFeatureViewProps {
  api: BrokerApi;
  release: ReleaseDto;
  featureId: string;
  onReleaseUpdated: (release: ReleaseDto) => void;
  testIdPrefix?: string;
}

/**
 * One feature's workflow, whatever its state. Looking never changes anything: the feature's own
 * data is re-pointed onto the existing stage screens (releaseForFeature), and unless it is the
 * active feature and unfinished they are read-only. Only the explicit "Resume work here" action
 * touches the workspace.
 */
export default function FeatureView({ api, release, featureId, onReleaseUpdated, testIdPrefix = "release" }: IFeatureViewProps) {
  const feature = release.features.find((f) => f.id === featureId);
  const view = useMemo(() => releaseForFeature(release, featureId), [release, featureId]);

  const [pipeline, setPipeline] = useState<PipelineStageDto[] | null>(null);
  const [pipelineError, setPipelineError] = useState<string | null>(null);
  const [resuming, setResuming] = useState(false);
  const [resumeError, setResumeError] = useState<string | null>(null);
  const [dismissedReadyRunId, setDismissedReadyRunId] = useState<string | null>(null);

  useEffect(() => {
    if (!feature) return;
    let cancelled = false;
    setPipelineError(null);
    (async () => {
      try {
        const p = await api.getPipelineAsync(featureId);
        if (!cancelled) setPipeline(p);
      } catch (e) {
        if (!cancelled) setPipelineError(toErrorMessage(e));
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, featureId, feature === undefined]);

  // Keep the view current while it is on screen; stops on its own when it unmounts.
  usePoller<ReleaseDto>({
    enabled: feature !== undefined,
    func: () => api.getReleaseAsync(release.id),
    onResult: (fresh) => { if (fresh) onReleaseUpdated(fresh); },
    onError: () => undefined,
    pollIntervalMilliseconds: 3000,
    maxIntervalMilliseconds: 20000,
    deps: [release.id, featureId],
  });

  const refreshRelease = useCallback(async () => {
    try {
      const fresh = await api.getReleaseAsync(release.id);
      if (fresh) onReleaseUpdated(fresh);
    } catch (e) {
      setPipelineError(toErrorMessage(e));
    }
  }, [api, release.id, onReleaseUpdated]);

  const handleResume = useCallback(async () => {
    setResuming(true);
    setResumeError(null);
    try {
      const fresh = await api.switchFeatureAsync(featureId);
      onReleaseUpdated(fresh);
    } catch {
      setResumeError("We couldn't resume this feature. Please try again.");
    } finally {
      setResuming(false);
    }
  }, [api, featureId, onReleaseUpdated]);

  const isDone = feature?.status === "Complete";
  const isActive = release.currentFeatureId === featureId;
  const readOnly = isDone || !isActive;

  const stages = pipeline ?? [];
  const currentIndex = isDone
    ? stages.length - 1
    : Math.min(Math.max(view.flowPosition?.currentStageIndex ?? 0, 0), Math.max(stages.length - 1, 0));
  const role = stages.length > 0 ? stages[currentIndex] : null;
  const run = role ? latestRunFor(view, role.name) : undefined;
  const nextStageName = currentIndex + 1 < stages.length ? stages[currentIndex + 1].name : null;

  const pendingSignoff = view.signoffs.filter(
    (s) => s.required && !s.approved && role !== null && s.stageName === role.name,
  );
  const showReview = !readOnly && pendingSignoff.length > 0 && run?.readyToProceed === true && run.id !== dismissedReadyRunId;
  const showApproveNow = !readOnly && pendingSignoff.length > 0 && run?.readyToProceed === true && !showReview;
  const { approve: approveNow, loading: approveLoading, error: approveError } =
    useApproveSignoff(featureId, pendingSignoff, api, onReleaseUpdated);

  useEffect(() => {
    if (!run?.readyToProceed) setDismissedReadyRunId(null);
  }, [run?.readyToProceed, run?.id]);

  if (!feature) {
    return (
      <div className={styles.notFound} data-testid={`${testIdPrefix}-feature-not-found`}>
        <p>We couldn&apos;t find that feature in this release.</p>
        <a href={buildRoute({ kind: "release", releaseId: release.id })}>Back to the release</a>
      </div>
    );
  }

  if (pipelineError && pipeline === null) {
    return <div className={releaseStyles.error} role="alert">{pipelineError}</div>;
  }

  if (pipeline === null) {
    return <div className={releaseStyles.loading} data-testid={`${testIdPrefix}-feature-loading`}>Loading…</div>;
  }

  const notStarted = !isDone && !isActive && (feature.status === "Proposed" || (!feature.flowPosition && (feature.stageRuns ?? []).length === 0));

  return (
    <div className={releaseStyles.detailView}>
      {isDone && (
        <div className={`${styles.banner} ${styles.bannerDone}`} data-testid={`${testIdPrefix}-feature-banner`}>
          <span>This feature is done. You&apos;re looking at its record — nothing here can be changed.</span>
        </div>
      )}

      {!isDone && !isActive && (
        <div className={styles.banner} data-testid={`${testIdPrefix}-feature-banner`}>
          <span>{notStarted ? "This feature hasn't started yet." : "This feature is paused. Resume work on it to continue."}</span>
          <ZestButton
            type="button"
            onClick={() => void handleResume()}
            disabled={resuming}
            data-testid={`${testIdPrefix}-resume-feature-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true, minBusyDurationMs: 0 }, visualOptions: { size: "sm" } }}
          >
            {resuming ? "Working…" : notStarted ? "Start working here" : "Resume work here"}
          </ZestButton>
        </div>
      )}

      {resumeError && <div className={releaseStyles.error} role="alert">{resumeError}</div>}

      <PipelineStepper pipeline={stages} stageIndex={isDone ? stages.length : currentIndex} signoffs={view.signoffs} />

      <StageScreen
        release={view}
        featureId={featureId}
        api={api}
        pipeline={stages}
        role={role}
        run={run}
        testIdPrefix={testIdPrefix}
        refreshRelease={refreshRelease}
        hidePrimaryPanel={showReview}
        readOnly={readOnly}
      />

      {showReview && run && (
        <StageCompleteCard
          featureId={featureId}
          stageRun={run}
          pendingSignoffs={pendingSignoff}
          api={api}
          testIdPrefix={testIdPrefix}
          nextStageName={nextStageName}
          onReleaseUpdated={onReleaseUpdated}
          onContinue={() => setDismissedReadyRunId(run.id)}
        />
      )}

      {showApproveNow && (
        <div className={releaseStyles.stageHandoff}>
          {approveError && <div className={releaseStyles.error}>{approveError}</div>}
          <ZestButton
            type="button"
            onClick={() => void approveNow()}
            disabled={approveLoading}
            data-testid={`${testIdPrefix}-approve-now-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
          >
            {approveLoading ? "Proceeding…" : proceedButtonLabel(nextStageName)}
          </ZestButton>
        </div>
      )}

      {view.stageRuns.length > 0 && (
        <details className={releaseStyles.timeline}>
          <summary>Stage history</summary>
          {view.stageRuns.map((sr) => (
            <StageHistoryCard key={sr.id} stageRun={sr} testIdPrefix={testIdPrefix} />
          ))}
        </details>
      )}
    </div>
  );
}
