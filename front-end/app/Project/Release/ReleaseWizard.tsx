import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { usePoller } from "../../Chat/Data/usePoller";
import {
  ActiveTurnInfo,
  MessageDto,
  ModelOption,
  PipelineStageDto,
  ReleaseDto,
  ReleaseGateCheckDto,
  ReleaseSignoffDto,
  ReleaseStageRunDto,
  ReviewFindingDto,
  StageArtifactDto,
} from "../../Chat/Data/BrokerTypes";
import MessageRow from "../../Chat/UI/MessageRow";
import RichText from "../../UI/RichText";
import ZestButton from "jattac.libs.web.zest-button";
import { ZestResponsiveLayout } from "jattac.libs.web.zest-responsive-layout";
import { FaSpinner } from "react-icons/fa6";
import { errorKindLabel, moveOnButtonLabel, phaseLabel, proceedButtonLabel, stageLabel, stageOutputLabel, statusLabel, whatsNext } from "./labels";
import { formatElapsed } from "../../UI/formatElapsed";
import styles from "../Styles/ReleaseWizard.module.css";

interface IReleaseWizardProps {
  api: BrokerApi;
  workspacePath: string;
  testIdPrefix?: string;
}

type WizardView = "list" | "create" | "detail";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function statusColor(status: string): string {
  switch (status) {
    case "Complete": return styles.statusComplete;
    case "InProgress": return styles.statusInProgress;
    case "Blocked": return styles.statusBlocked;
    case "BlockedGate": return styles.statusBlocked;
    case "BlockedEntry": return styles.statusBlocked;
    case "BlockedSignoff": return styles.statusBlocked;
    case "Escalated": return styles.statusBlocked;
    case "Ready": return styles.statusReady;
    default: return "";
  }
}

function shouldPollStage(status: string): boolean {
  return status === "Active" || status === "GatesRunning" || status === "BlockedGate" || status === "BlockedEntry" || status === "BlockedSignoff" || status === "Escalated";
}

function latestRunFor(release: ReleaseDto, stageName: string): ReleaseStageRunDto | undefined {
  const runs = release.stageRuns.filter((sr) => sr.stageName === stageName);
  if (runs.length === 0) return undefined;
  return [...runs].sort((a, b) => (b.startedAt ?? "").localeCompare(a.startedAt ?? ""))[0];
}

// Deterministic, no LLM: joins the actual failed gate-check/finding evidence already on the
// stage run into a starting draft, so a novice reviews/edits real failure detail instead of
// transcribing cryptic stack traces into a blank box themselves.
function draftPushBackInstructions(stageRun: ReleaseStageRunDto): string {
  const lines: string[] = [];
  for (const gc of stageRun.gateChecks) {
    if (!gc.passed) lines.push(`${gc.name}: ${gc.evidenceText ?? "failed"}`);
  }
  for (const f of stageRun.findings) {
    lines.push(`${f.severity} finding in ${f.target}: ${f.summary}`);
  }
  return lines.join("\n\n");
}

export default function ReleaseWizard({ api, workspacePath, testIdPrefix = "release" }: IReleaseWizardProps) {
  const [view, setView] = useState<WizardView>("list");
  const [releases, setReleases] = useState<ReleaseDto[]>([]);
  const [selectedRelease, setSelectedRelease] = useState<ReleaseDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [featureKey, setFeatureKey] = useState("");

  const loadReleases = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const list = await api.listReleasesAsync(workspacePath);
      setReleases(list);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath]);

  useEffect(() => {
    if (view === "list") loadReleases();
  }, [view, loadReleases]);

  const handleCreate = useCallback(async () => {
    if (!featureKey.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const release = await api.createReleaseAsync(featureKey.trim(), workspacePath);
      setSelectedRelease(release);
      setView("detail");
      setFeatureKey("");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, featureKey, workspacePath]);

  const handleSelectRelease = useCallback(async (releaseId: string) => {
    setLoading(true);
    setError(null);
    try {
      const release = await api.getReleaseAsync(releaseId);
      setSelectedRelease(release);
      setView("detail");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api]);

  const handleRefresh = useCallback(async () => {
    if (!selectedRelease) return;
    setLoading(true);
    setError(null);
    try {
      const updated = await api.getReleaseAsync(selectedRelease.id);
      setSelectedRelease(updated);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, selectedRelease]);

  return (
    <div className={styles.container} data-testid={testIdPrefix}>
      {error && <div className={styles.error}>{error}</div>}

      {view === "list" && (
        <div className={styles.listView}>
          <div className={styles.header}>
            <h2>Releases</h2>
            <ZestButton
              type="button"
              onClick={() => setView("create")}
              disabled={loading}
              zest={{ semanticType: "add", visualOptions: { size: "sm" } }}
            >
              New Release
            </ZestButton>
          </div>
          {loading && <div className={styles.loading}>Loading...</div>}
          <div className={styles.releaseList}>
            {releases.map((r) => (
              <div
                key={r.id}
                className={styles.releaseItem}
                onClick={() => handleSelectRelease(r.id)}
                data-testid={`${testIdPrefix}-item-${r.id}`}
              >
                <span className={styles.releaseTitle}>{r.title ?? r.features[0]?.key ?? r.id.slice(0, 8)}</span>
                <span className={`${styles.releaseStatus} ${statusColor(r.status)}`}>{statusLabel(r.status)}</span>
                <span className={styles.releaseStages}>
                  {r.flowPosition?.currentStageName ? stageLabel(r.flowPosition.currentStageName) : "—"}
                </span>
              </div>
            ))}
            {releases.length === 0 && !loading && <div className={styles.empty}>No releases yet.</div>}
          </div>
        </div>
      )}

      {view === "create" && (
        <div className={styles.createView}>
          <div className={styles.header}>
            <h2>New Release</h2>
            <ZestButton type="button" onClick={() => setView("list")} disabled={loading}
              zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}>Back</ZestButton>
          </div>
          <div className={styles.form}>
            <label>
              Feature Key
              <input
                value={featureKey}
                onChange={(e) => setFeatureKey(e.target.value)}
                placeholder="e.g. login-form"
                data-testid={`${testIdPrefix}-feature-key`}
              />
            </label>
            <ZestButton
              type="button"
              onClick={handleCreate}
              disabled={!featureKey.trim()}
              data-testid={`${testIdPrefix}-create-btn`}
              zest={{ semanticType: "save", busyOptions: { preventRageClick: true } }}
            >
              {loading ? "Creating..." : "Create Release"}
            </ZestButton>
          </div>
        </div>
      )}

      {view === "detail" && selectedRelease && (
        <ReleaseDetail
          release={selectedRelease}
          api={api}
          testIdPrefix={testIdPrefix}
          loading={loading}
          onBack={() => { setView("list"); setSelectedRelease(null); }}
          onRefresh={handleRefresh}
          onReleaseUpdated={setSelectedRelease}
        />
      )}
    </div>
  );
}

// ─── release detail with per-stage screens ─────────────────────────────────

interface IReleaseDetailProps {
  release: ReleaseDto;
  api: BrokerApi;
  testIdPrefix: string;
  loading: boolean;
  onBack: () => void;
  onRefresh: () => void;
  onReleaseUpdated: (release: ReleaseDto) => void;
}

function ReleaseDetail({ release, api, testIdPrefix, loading, onBack, onRefresh, onReleaseUpdated }: IReleaseDetailProps) {
  const featureId = release.currentFeatureId ?? null;
  const [pipeline, setPipeline] = useState<PipelineStageDto[]>([]);
  const [pipelineError, setPipelineError] = useState<string | null>(null);
  const [revealError, setRevealError] = useState<string | null>(null);

  const handleRevealWorkspace = useCallback(async () => {
    setRevealError(null);
    try {
      await api.revealInExplorerAsync(release.workspacePath);
    } catch (e) {
      setRevealError(toErrorMessage(e));
    }
  }, [api, release.workspacePath]);

  const loadPipeline = useCallback(async () => {
    if (!featureId) { setPipeline([]); return; }
    setPipelineError(null);
    try {
      const p = await api.getPipelineAsync(featureId);
      setPipeline(p);
    } catch (e) {
      setPipelineError(toErrorMessage(e));
    }
  }, [api, featureId]);

  useEffect(() => {
    void loadPipeline();
  }, [loadPipeline]);

  const refreshRelease = useCallback(async () => {
    try {
      const fresh = await api.getReleaseAsync(release.id);
      onReleaseUpdated(fresh);
    } catch (e) {
      setPipelineError(toErrorMessage(e));
    }
  }, [api, release.id, onReleaseUpdated]);

  const stageIndex = useMemo(() => {
    if (pipeline.length === 0) return release.flowPosition?.currentStageIndex ?? 0;
    const fp = release.flowPosition;
    if (release.status === "Ready" || release.status === "Complete") return pipeline.length;
    if (fp && fp.currentStageIndex >= 0 && fp.currentStageIndex < pipeline.length) return fp.currentStageIndex;
    if (fp) return Math.min(fp.currentStageIndex, pipeline.length - 1);
    return 0;
  }, [pipeline, release]);

  const role = stageIndex < pipeline.length ? pipeline[stageIndex] : null;
  const run = role ? latestRunFor(release, role.name) : undefined;
  const nextStageName = stageIndex + 1 < pipeline.length ? pipeline[stageIndex + 1].name : null;

  // Background sync so status changes (gates passing, signoff becoming required, a push-back)
  // show up on their own — no manual Refresh needed, and no exclusion for interactive stages
  // like the old interval had (that exclusion was the root cause of BA never re-syncing).
  // maxConsecutiveErrors is intentionally left unset: if the backend restarts, this keeps
  // retrying at its backed-off interval and quietly resumes the moment it's back, rather than
  // giving up and requiring a hard reload.
  const { pollNow: pollReleaseNow } = usePoller<ReleaseDto>({
    enabled: featureId !== null,
    func: () => api.getReleaseAsync(release.id),
    onResult: (fresh) => {
      setPipelineError(null);
      onReleaseUpdated(fresh);
    },
    onError: (e) => setPipelineError(toErrorMessage(e)),
    pollIntervalMilliseconds: 3000,
    maxIntervalMilliseconds: 20000,
    deps: [release.id, featureId],
  });

  const pendingCurrentSignoff = release.signoffs.filter(
    (s) => s.required && !s.approved && role !== null && s.stageName === role.name,
  );

  // "Continue working" dismisses the stage-complete card without sending a message, so the
  // user can re-read the chat without the card in the way. It only hides the card itself —
  // the always-visible approve control below (rendered whenever the stage is still ready)
  // means dismissing never removes the only way to actually proceed.
  const [dismissedReadyRunId, setDismissedReadyRunId] = useState<string | null>(null);
  useEffect(() => {
    if (!run?.readyToProceed) setDismissedReadyRunId(null);
  }, [run?.readyToProceed, run?.id]);

  const showReview = pendingCurrentSignoff.length > 0 && run?.readyToProceed === true && run.id !== dismissedReadyRunId;
  const showApproveNow = pendingCurrentSignoff.length > 0 && run?.readyToProceed === true && !showReview;

  const handleRefreshClick = useCallback(() => {
    setDismissedReadyRunId(null);
    pollReleaseNow();
    onRefresh();
  }, [onRefresh, pollReleaseNow]);

  const {
    approve: approveNow,
    loading: approveNowLoading,
    error: approveNowError,
  } = useApproveSignoff(featureId ?? "", pendingCurrentSignoff, api, onReleaseUpdated);

  return (
    <div className={styles.detailView}>
      <div className={styles.header}>
        <ZestButton type="button" onClick={onBack} disabled={loading}
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}>Back</ZestButton>
        <h2>{release.title ?? release.features[0]?.key}</h2>
        <span className={`${styles.releaseStatus} ${statusColor(release.status)}`}>{statusLabel(release.status)}</span>
        <ZestButton type="button" onClick={handleRefreshClick} disabled={loading}
          zest={{ semanticType: "refresh", busyOptions: { preventRageClick: true }, buttonStyle: "text", visualOptions: { size: "sm" } }}>
          Refresh
        </ZestButton>
      </div>

      <details className={styles.advancedDetails}>
        <summary>Advanced details</summary>
        <div className={styles.detailInfo}>
          <span>ID: {release.id.slice(0, 8)}...</span>
          <span>
            Workspace:{" "}
            <button
              type="button"
              className={styles.workspaceLink}
              data-testid={`${testIdPrefix}-reveal-workspace-btn`}
              onClick={handleRevealWorkspace}
              title="Open in file explorer"
            >
              {release.workspacePath}
            </button>
          </span>
          <span>Version: {release.version}</span>
        </div>
        {revealError && <div className={styles.error}>{revealError}</div>}
      </details>

      {!featureId && (
        <CreateFeatureForm
          release={release}
          api={api}
          testIdPrefix={testIdPrefix}
          onReleaseUpdated={onReleaseUpdated}
        />
      )}

      {featureId && (
        <>
          {pipeline.length > 0 && (
            <PipelineStepper pipeline={pipeline} stageIndex={stageIndex} signoffs={release.signoffs} />
          )}

          {pipelineError && <div className={styles.error}>{pipelineError}</div>}

          <StageScreen
            release={release}
            featureId={featureId}
            api={api}
            pipeline={pipeline}
            role={role}
            run={run}
            testIdPrefix={testIdPrefix}
            refreshRelease={refreshRelease}
            hidePrimaryPanel={showReview}
          />

          {showReview && run && (
            <StageCompleteCard
              featureId={featureId}
              stageRun={run}
              pendingSignoffs={pendingCurrentSignoff}
              api={api}
              testIdPrefix={testIdPrefix}
              nextStageName={nextStageName}
              onReleaseUpdated={onReleaseUpdated}
              onContinue={() => setDismissedReadyRunId(run.id)}
            />
          )}

          {showApproveNow && (
            <div className={styles.stageHandoff}>
              {approveNowError && <div className={styles.error}>{approveNowError}</div>}
              <ZestButton
                type="button"
                onClick={() => void approveNow()}
                disabled={approveNowLoading}
                data-testid={`${testIdPrefix}-approve-now-btn`}
                zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
              >
                {approveNowLoading ? "Proceeding…" : proceedButtonLabel(nextStageName)}
              </ZestButton>
            </div>
          )}

          {release.stageRuns.length > 0 && (
            <details className={styles.timeline}>
              <summary>Stage history</summary>
              {release.stageRuns.map((sr) => (
                <StageHistoryCard key={sr.id} stageRun={sr} testIdPrefix={testIdPrefix} />
              ))}
            </details>
          )}
        </>
      )}

      {release.features.length > 0 && (
        <FeatureList
          release={release}
          activeFeatureId={featureId}
          api={api}
          testIdPrefix={testIdPrefix}
          onReleaseUpdated={onReleaseUpdated}
        />
      )}
    </div>
  );
}

// ─── feature list: every feature in the release, regardless of status, with a ──
// switch action — a release can have many concurrently-open features (7A); only
// one is ever the workspace's active checkout at a time (7B), so switching away
// from it parks its WIP via a git stash and restores whichever stash (if any)
// belongs to the feature being switched to.

interface IFeatureListProps {
  release: ReleaseDto;
  activeFeatureId: string | null;
  api: BrokerApi;
  testIdPrefix: string;
  onReleaseUpdated: (release: ReleaseDto) => void;
}

function FeatureList({ release, activeFeatureId, api, testIdPrefix, onReleaseUpdated }: IFeatureListProps) {
  const [switchingId, setSwitchingId] = useState<string | null>(null);
  const [switchError, setSwitchError] = useState<string | null>(null);
  const [addingFeature, setAddingFeature] = useState(false);

  const handleSwitch = useCallback(async (featureId: string) => {
    setSwitchingId(featureId);
    setSwitchError(null);
    try {
      const fresh = await api.switchFeatureAsync(featureId);
      onReleaseUpdated(fresh);
    } catch (e) {
      setSwitchError(toErrorMessage(e));
    } finally {
      setSwitchingId(null);
    }
  }, [api, onReleaseUpdated]);

  const handleFeatureAdded = useCallback((fresh: ReleaseDto) => {
    onReleaseUpdated(fresh);
    setAddingFeature(false);
  }, [onReleaseUpdated]);

  return (
    <details className={styles.timeline} open data-testid={`${testIdPrefix}-feature-list`}>
      <summary>Features ({release.features.length})</summary>
      {switchError && <div className={styles.error}>{switchError}</div>}
      {release.features.map((f) => {
        const isActive = f.id === activeFeatureId;
        const canSwitch = !isActive && f.status !== "Complete";
        return (
          <div key={f.id} className={styles.stageCard} data-testid={`${testIdPrefix}-feature-${f.key}`}>
            <span className={styles.stageName}>{f.key}</span>
            <span className={`${styles.releaseStatus} ${statusColor(f.status)}`}>{statusLabel(f.status)}</span>
            <span className={styles.questionBadge}>{f.branchName}</span>
            {isActive && <span className={styles.stepTick} data-testid={`${testIdPrefix}-feature-${f.key}-active`}>✓ active</span>}
            {canSwitch && (
              <ZestButton
                type="button"
                onClick={() => void handleSwitch(f.id)}
                disabled={switchingId !== null}
                data-testid={`${testIdPrefix}-switch-feature-${f.key}`}
                zest={{ buttonStyle: "outline", visualOptions: { size: "sm" }, busyOptions: { preventRageClick: true } }}
              >
                {switchingId === f.id ? "Switching..." : "Switch to this feature"}
              </ZestButton>
            )}
          </div>
        );
      })}
      {addingFeature ? (
        <CreateFeatureForm
          release={release}
          api={api}
          testIdPrefix={testIdPrefix}
          onReleaseUpdated={handleFeatureAdded}
        />
      ) : (
        <ZestButton
          type="button"
          onClick={() => setAddingFeature(true)}
          data-testid={`${testIdPrefix}-add-feature-btn`}
          zest={{ semanticType: "add", buttonStyle: "text", visualOptions: { size: "sm" } }}
        >
          Add another feature
        </ZestButton>
      )}
    </details>
  );
}

// ─── create feature (GitFlow gate before coding can start) ────────────────

interface ICreateFeatureFormProps {
  release: ReleaseDto;
  api: BrokerApi;
  testIdPrefix: string;
  onReleaseUpdated: (release: ReleaseDto) => void;
}

function CreateFeatureForm({ release, api, testIdPrefix, onReleaseUpdated }: ICreateFeatureFormProps) {
  const [featureKey, setFeatureKey] = useState("");
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleCreate = useCallback(async () => {
    if (!featureKey.trim()) return;
    setCreating(true);
    setError(null);
    try {
      await api.createFeatureAsync(release.id, featureKey.trim());
      const fresh = await api.getReleaseAsync(release.id);
      onReleaseUpdated(fresh);
      setFeatureKey("");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setCreating(false);
    }
  }, [api, release.id, featureKey, onReleaseUpdated]);

  return (
    <div className={styles.createView}>
      <div className={styles.form}>
        <p className={styles.whatsNext}>
          Create a feature to start coding against this release. Each feature gets its own git branch,
          and merges back into the release branch once it passes QA and signoff.
        </p>
        <label>
          Feature Key
          <input
            value={featureKey}
            onChange={(e) => setFeatureKey(e.target.value)}
            placeholder="e.g. password-reset"
            data-testid={`${testIdPrefix}-new-feature-key`}
          />
        </label>
        {error && <div className={styles.error}>{error}</div>}
        <ZestButton
          type="button"
          onClick={() => void handleCreate()}
          disabled={!featureKey.trim() || creating}
          data-testid={`${testIdPrefix}-create-feature-btn`}
          zest={{ semanticType: "save", busyOptions: { preventRageClick: true } }}
        >
          {creating ? "Creating..." : "Create Feature"}
        </ZestButton>
      </div>
    </div>
  );
}

// ─── pipeline stepper ──────────────────────────────────────────────────────

function PipelineStepper({
  pipeline,
  stageIndex,
  signoffs,
}: {
  pipeline: PipelineStageDto[];
  stageIndex: number;
  signoffs: ReleaseDto["signoffs"];
}) {
  return (
    <div className={styles.stepper} data-testid="pipeline-stepper">
      {pipeline.map((p, i) => {
        let cls = styles.stepChip;
        if (i < stageIndex) cls += ` ${styles.stepChipDone}`;
        else if (i === stageIndex) cls += ` ${styles.stepChipCurrent}`;
        else cls += ` ${styles.stepChipPending}`;
        const approvedSignoff = signoffs.some((s) => s.required && s.approved && s.stageName === p.name);
        return (
          <React.Fragment key={p.name}>
            {i > 0 && <span className={styles.stepSeparator}>›</span>}
            <span className={cls} data-testid={`pipeline-step-${p.name}`}>
              {i < stageIndex && <span className={styles.stepTick}>✓ </span>}
              {i + 1} {stageLabel(p.name)}
              {p.signoff && <span className={styles.stepSignoff}>{approvedSignoff ? " · approved" : " · review needed"}</span>}
            </span>
          </React.Fragment>
        );
      })}
    </div>
  );
}

// ─── stage screen (dedicated view for the current stage) ───────────────────

interface IStageScreenProps {
  release: ReleaseDto;
  featureId: string;
  api: BrokerApi;
  pipeline: PipelineStageDto[];
  role: PipelineStageDto | null;
  run: ReleaseStageRunDto | undefined;
  testIdPrefix: string;
  refreshRelease: () => Promise<void>;
  hidePrimaryPanel?: boolean;
}

function StageScreen({ release, featureId, api, pipeline, role, run, testIdPrefix, refreshRelease, hidePrimaryPanel = false }: IStageScreenProps) {
  const [busy, setBusy] = useState(false);
  const [stageError, setStageError] = useState<string | null>(null);
  // Sparse-by-default, full-detail-on-demand: the header/checklist only ever show compact
  // ✓/✗ ticks and a status word — everything behind them (full gate evidence, every
  // guidance note, the specific escalation reason) lives here, reached by clicking the
  // status badge, instead of cluttering the primary view.
  const [diagnosticsOpen, setDiagnosticsOpen] = useState(false);

  const handleStartStage = useCallback(async () => {
    setBusy(true);
    setStageError(null);
    try {
      await api.startStageAsync(featureId);
      await refreshRelease();
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }, [api, featureId, refreshRelease]);

  const handleRunStage = useCallback(async () => {
    setBusy(true);
    setStageError(null);
    try {
      await api.runStageAsync(featureId);
      await refreshRelease();
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }, [api, featureId, refreshRelease]);

  if (release.status === "Ready" || release.status === "Complete" || !role) {
    return (
      <div className={styles.stagePanel}>
        <div className={styles.doneBanner}>
          <strong>Release complete.</strong> All stages finished successfully.
        </div>
      </div>
    );
  }

  const interactive = role.userInputRequired;
  const roleIndex = pipeline.findIndex((p) => p.name === role.name);
  const nextStageName = roleIndex >= 0 && roleIndex + 1 < pipeline.length ? pipeline[roleIndex + 1].name : null;
  // A push-back moves the flow position back to an earlier stage without touching that
  // stage's own (already Complete) run row — nothing else ever advances the position onto a
  // stage whose current run is Complete, so this combination only happens via push-back, and
  // means "this is the current stage again, but it has no fresh work in flight yet."
  const needsFreshStart = !run || run.status === "Complete";

  return (
    <ZestResponsiveLayout
      sidePaneWidth="480px"
      sidePane={{
        visible: diagnosticsOpen,
        title: "Stage diagnostics",
        content: run ? <StageDiagnosticsContent run={run} /> : null,
        onClose: () => setDiagnosticsOpen(false),
      }}
    >
    <div className={styles.stagePanel}>
      <div className={styles.stageHeader}>
        <span className={styles.stageName}>Current Stage: {stageLabel(role.name)}</span>
        {run && (
          <>
            <button
              type="button"
              className={styles.phaseBadge}
              onClick={() => setDiagnosticsOpen(true)}
              data-testid={`${testIdPrefix}-diagnostics-btn`}
            >
              {phaseLabel(run.phase)}
            </button>
            <span className={styles.questionBadge}>attempt {run.attempt}</span>
          </>
        )}
        {run?.acpSessionId && (
          <ModelPicker releaseId={release.id} sessionId={run.acpSessionId} api={api} testIdPrefix={testIdPrefix} />
        )}
      </div>

      {!hidePrimaryPanel && !needsFreshStart && run && role.steps.length > 0 && (
        <StepChecklist
          testIdPrefix={testIdPrefix}
          steps={role.steps}
          gateChecks={run.gateChecks}
          inProgress={!["BlockedGate", "BlockedEntry", "BlockedSignoff", "Complete"].includes(run.status)}
        />
      )}

      {!hidePrimaryPanel && whatsNext(role.name) && <div className={styles.whatsNext}>{whatsNext(role.name)}</div>}

      {stageError && <div className={styles.error}>{stageError}</div>}

      {!hidePrimaryPanel && needsFreshStart && (
        <div className={styles.noRun}>
          {interactive ? (
            <>
              <div className={styles.noRunHint}>
                This stage runs as a conversation with the agent. Start it to begin the dialogue.
              </div>
              <ZestButton
                type="button"
                onClick={handleStartStage}
                disabled={busy}
                data-testid={`${testIdPrefix}-start-stage-btn`}
                zest={{ semanticType: "submit", busyOptions: { preventRageClick: true } }}
              >
                {busy ? "Starting..." : "Start Conversation"}
              </ZestButton>
            </>
          ) : (
            <AutoRunNotice testIdPrefix={testIdPrefix} onRun={handleRunStage} />
          )}
        </div>
      )}

      {!hidePrimaryPanel && !needsFreshStart && run && interactive && (
        <ChatStage
          key={run.id}
          featureId={featureId}
          stageRun={run}
          api={api}
          testIdPrefix={testIdPrefix}
          busy={busy}
          runStage={handleRunStage}
          refreshRelease={refreshRelease}
          nextStageName={nextStageName}
        />
      )}

      {!hidePrimaryPanel && !needsFreshStart && run && !interactive && (
        <StageLog
          key={run.id}
          release={release}
          stageRun={run}
          api={api}
          testIdPrefix={testIdPrefix}
          busy={busy}
          runStage={handleRunStage}
          refreshRelease={refreshRelease}
        />
      )}

      {!hidePrimaryPanel && roleIndex > 0 && (
        <StageContextPanels
          featureId={featureId}
          api={api}
          testIdPrefix={testIdPrefix}
          previousRoleName={pipeline[roleIndex - 1].name}
          previousRun={latestRunFor(release, pipeline[roleIndex - 1].name)}
        />
      )}

      {run && (run.status === "BlockedGate" || run.status === "BlockedEntry") && pipeline.length > 0 && (
        <PushBackPanel
          featureId={featureId}
          api={api}
          stageIndex={release.flowPosition?.currentStageIndex ?? 0}
          pipeline={pipeline}
          stageRun={run}
          testIdPrefix={testIdPrefix}
          refreshRelease={refreshRelease}
        />
      )}
    </div>
    </ZestResponsiveLayout>
  );
}

// ─── stage diagnostics (on-demand full detail behind the status badge) ────

function GateCheckItem({ gateCheck, currentStageName }: { gateCheck: ReleaseGateCheckDto; currentStageName: string }) {
  // Only worth surfacing when it differs from the obvious default (the stage that ran the
  // check owns fixing it) — that's the whole point of ResponsibleRole existing at all.
  const ownedElsewhere = gateCheck.responsibleRole && gateCheck.responsibleRole !== currentStageName;
  return (
    <li>
      <div>
        {gateCheck.passed ? "✓" : "✗"} {gateCheck.name}
        {ownedElsewhere && (
          <span className={styles.diagnosticsOwner}> — owned by {stageLabel(gateCheck.responsibleRole!)}</span>
        )}
      </div>
      {gateCheck.evidenceText && <pre className={styles.diagnosticsEvidence}>{gateCheck.evidenceText}</pre>}
    </li>
  );
}

function StageDiagnosticsContent({ run }: { run: ReleaseStageRunDto }) {
  const entryChecks = run.gateChecks.filter((gc) => gc.isEntryGate);
  const exitChecks = run.gateChecks.filter((gc) => !gc.isEntryGate);

  return (
    <div className={styles.diagnosticsContent}>
      <section>
        <h4>Status</h4>
        <p>{statusLabel(run.status)}</p>
        {run.status === "Escalated" && (
          <p className={styles.diagnosticsError}>
            {errorKindLabel(run.lastErrorKind) || run.lastErrorMessage || "The agent hit an error."}
          </p>
        )}
        {run.status === "BlockedEntry" && (
          <p className={styles.diagnosticsError}>
            An entry check failed before this stage&apos;s turn started — see Entry checks below.
          </p>
        )}
      </section>

      <section>
        <h4>Entry checks</h4>
        {entryChecks.length === 0 ? (
          <p>No entry checks for this stage.</p>
        ) : (
          <ul className={styles.diagnosticsList}>
            {entryChecks.map((gc) => (
              <GateCheckItem key={gc.id} gateCheck={gc} currentStageName={run.stageName} />
            ))}
          </ul>
        )}
      </section>

      <section>
        <h4>Exit checks</h4>
        {exitChecks.length === 0 ? (
          <p>No exit checks yet.</p>
        ) : (
          <ul className={styles.diagnosticsList}>
            {exitChecks.map((gc) => (
              <GateCheckItem key={gc.id} gateCheck={gc} currentStageName={run.stageName} />
            ))}
          </ul>
        )}
      </section>

      <section>
        <h4>Guidance notes</h4>
        {run.guidanceNotes.length === 0 ? (
          <p>No guidance notes yet.</p>
        ) : (
          <ul className={styles.diagnosticsList}>
            {run.guidanceNotes.map((note) => (
              <li key={note.id}>
                <div className={styles.diagnosticsNoteAttribution}>
                  {note.addedBy === "system:gate-failure" ? "Auto-generated from gate failure" : note.addedBy ?? "user"}
                </div>
                <div>{note.text}</div>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h4>Specialist consultations</h4>
        {run.specialistConsultations.length === 0 ? (
          <p>No specialists consulted yet.</p>
        ) : (
          <ul className={styles.diagnosticsList}>
            {run.specialistConsultations.map((consultation) => (
              <li key={consultation.id}>
                <div className={styles.diagnosticsNoteAttribution}>Consulted: {stageLabel(consultation.specialistName)}</div>
                <div>{consultation.question}</div>
                <pre className={styles.diagnosticsEvidence}>{consultation.responseText}</pre>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

// ─── auto-run notice (fully automated stages: nothing for the user to do) ──
//
// A non-interactive stage has no user input to wait for — nothing to configure,
// nothing to review before it runs — so it starts the instant it becomes the
// current stage, with no click required. This covers both a stage's first-ever
// run and landing back on one via push-back (see needsFreshStart above), since
// from the user's perspective both are "I just arrived here, there's nothing
// for me to do but wait." It fires once per mount only; a failed run's "Run
// Stage Again" always stays a manual, explicit click — auto-retrying against
// code that's still broken before the user has read why it failed or pushed
// back would just waste a run.

interface IAutoRunNoticeProps {
  testIdPrefix: string;
  onRun: () => void;
}

function AutoRunNotice({ testIdPrefix, onRun }: IAutoRunNoticeProps) {
  const firedRef = useRef(false);
  const onRunRef = useRef(onRun);
  onRunRef.current = onRun;

  useEffect(() => {
    if (firedRef.current) return;
    firedRef.current = true;
    onRunRef.current();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className={styles.noRunHint} data-testid={`${testIdPrefix}-auto-run-notice`}>
      This stage runs autonomously — starting automatically…
    </div>
  );
}

// ─── step checklist: known steps ticked off as they complete ──────────────
//
// A role's steps are fully known ahead of time from the workflow definition — only
// completion is dynamic. A step counts as done once its name appears among the
// polled gateChecks (Agent steps never get their own gate check, so they're
// inferred done once a later step's check appears). The first not-yet-done step
// gets the spinner, but only while the stage is actually still in progress —
// once blocked/complete, nothing is running, so nothing should spin.

interface IStepChecklistProps {
  testIdPrefix: string;
  steps: string[];
  gateChecks: ReleaseGateCheckDto[];
  inProgress: boolean;
}

function StepChecklist({ testIdPrefix, steps, gateChecks, inProgress }: IStepChecklistProps) {
  let currentAssigned = false;

  return (
    <ul className={styles.stepChecklist} data-testid={`${testIdPrefix}-step-checklist`}>
      {steps.map((name) => {
        const check = gateChecks.find((gc) => gc.name === name);
        let status: "done" | "current" | "pending";
        if (check) {
          status = "done";
        } else if (inProgress && !currentAssigned) {
          currentAssigned = true;
          status = "current";
        } else {
          status = "pending";
        }

        return (
          <li
            key={name}
            data-testid={`${testIdPrefix}-step-${name}`}
            data-status={status}
            className={`${styles.stepChecklistItem} ${status === "done" ? (check?.passed === false ? styles.stepFailed : styles.stepDone) : status === "current" ? styles.stepCurrent : styles.stepPending}`}
          >
            {status === "done" && (check?.passed === false ? "✗ " : "✓ ")}
            {status === "current" && <FaSpinner className={styles.spinIcon} aria-hidden="true" />}
            {" "}{name}
          </li>
        );
      })}
    </ul>
  );
}

// ─── stage context: previous stage's output (reference) + live changed files ──
//
// Deterministic and LLM-free by design: nothing here infers which requirement is
// "active" — it just surfaces the previous stage's completed artifacts alongside
// the current stage's live file changes, so the user can make that connection
// themselves. Shown for every stage except the first (nothing precedes it).

interface IStageContextPanelsProps {
  featureId: string;
  api: BrokerApi;
  testIdPrefix: string;
  previousRoleName: string;
  previousRun: ReleaseStageRunDto | undefined;
}

function StageContextPanels({ featureId, api, testIdPrefix, previousRoleName, previousRun }: IStageContextPanelsProps) {
  const [artifacts, setArtifacts] = useState<StageArtifactDto[]>([]);

  useEffect(() => {
    if (!previousRun) {
      setArtifacts([]);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const result = await api.getStageArtifactsAsync(featureId, previousRun.id);
        if (!cancelled) setArtifacts(result);
      } catch {
        // Best-effort reference panel — a failed fetch just leaves it empty.
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, featureId, previousRun?.id]);

  const [changedFiles, setChangedFiles] = useState<string[]>([]);
  usePoller<string[]>({
    enabled: true,
    func: () => api.getWorkspaceChangesAsync(featureId),
    onResult: (files) => setChangedFiles(files),
    pollIntervalMilliseconds: 3000,
    maxIntervalMilliseconds: 15000,
    deps: [featureId],
  });

  return (
    <div className={styles.contextPanels}>
      <details className={styles.advancedDetails} data-testid={`${testIdPrefix}-previous-stage-panel`}>
        <summary>{stageOutputLabel(previousRoleName)} (reference)</summary>
        {artifacts.length === 0 && <div className={styles.stageSummary}>No artifacts yet.</div>}
        {artifacts.map((a) => (
          <div key={a.relativePath}>
            <div className={styles.detailInfo}>{a.relativePath}</div>
            {a.content != null && <pre className={styles.detailInfo}>{a.content}</pre>}
          </div>
        ))}
      </details>

      <div className={styles.advancedDetails} data-testid={`${testIdPrefix}-changed-files-panel`}>
        <div>Files changed this attempt:</div>
        {changedFiles.length === 0 ? (
          <div className={styles.stageSummary}>No changes yet.</div>
        ) : (
          <ul className={styles.changedFilesList}>
            {changedFiles.map((f) => <li key={f}>{f}</li>)}
          </ul>
        )}
      </div>
    </div>
  );
}

// ─── model picker (let a non-technical user swap the AI engine) ───────────

interface IModelPickerProps {
  releaseId: string;
  sessionId: string;
  api: BrokerApi;
  testIdPrefix: string;
}

function ModelPicker({ releaseId, sessionId, api, testIdPrefix }: IModelPickerProps) {
  const [models, setModels] = useState<ModelOption[]>([]);
  const [currentModelId, setCurrentModelId] = useState<string | null>(null);
  const [switching, setSwitching] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [available, session] = await Promise.all([
          api.getAvailableModelsAsync(releaseId),
          api.getSessionAsync(sessionId),
        ]);
        if (cancelled) return;
        setModels(available);
        setCurrentModelId(session.modelId ?? available[0]?.value ?? null);
      } catch (e) {
        if (!cancelled) setError(toErrorMessage(e));
      }
    })();
    return () => { cancelled = true; };
  }, [api, releaseId, sessionId]);

  const handleChange = useCallback(async (e: React.ChangeEvent<HTMLSelectElement>) => {
    const modelId = e.target.value;
    setSwitching(true);
    setError(null);
    try {
      await api.setModelAsync(sessionId, modelId);
      setCurrentModelId(modelId);
    } catch (err) {
      setError(toErrorMessage(err));
    } finally {
      setSwitching(false);
    }
  }, [api, sessionId]);

  if (models.length === 0) return null;

  return (
    <div className={styles.modelPicker}>
      <label className={styles.modelPickerLabel}>
        AI Engine
        <select
          value={currentModelId ?? ""}
          onChange={(e) => void handleChange(e)}
          disabled={switching}
          data-testid={`${testIdPrefix}-model-picker`}
        >
          {models.map((m) => (
            <option key={m.value} value={m.value}>{m.name}</option>
          ))}
        </select>
      </label>
      {error && <div className={styles.error}>{error}</div>}
    </div>
  );
}

// ─── interactive chat stage (mirrors the Chat tab for this stage) ──────────

interface IChatStageProps {
  featureId: string;
  stageRun: ReleaseStageRunDto;
  api: BrokerApi;
  testIdPrefix: string;
  busy: boolean;
  runStage: () => void;
  refreshRelease: () => Promise<void>;
  nextStageName: string | null;
}

function ChatStage({ featureId, stageRun, api, testIdPrefix, busy, runStage, refreshRelease, nextStageName }: IChatStageProps) {
  const [messages, setMessages] = useState<MessageDto[]>([]);
  const [inspecting, setInspecting] = useState<MessageDto | null>(null);
  const [input, setInput] = useState("");
  const [sending, setSending] = useState(false);
  const [gatesRunning, setGatesRunning] = useState(false);
  const [messagesError, setMessagesError] = useState<string | null>(null);
  const chatContainerRef = useRef<HTMLDivElement>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const gatesInFlight = useRef(false);
  const lastAutoRunMsgId = useRef<string | null>(null);
  // Only auto-follow new messages while the reader is already at (or near) the bottom —
  // otherwise scrolling up to review earlier output keeps getting yanked back down.
  const stickToBottomRef = useRef(true);

  const handleChatScroll = useCallback(() => {
    const el = chatContainerRef.current;
    if (!el) return;
    stickToBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
  }, []);

  const hasStandaloneDone = useCallback((text: string): boolean => {
    return text.split("\n").some((line) => /^\s*done\s*$/i.test(line));
  }, []);

  const runGatesNow = useCallback(async () => {
    if (gatesInFlight.current) return;
    gatesInFlight.current = true;
    setGatesRunning(true);
    try {
      await api.runStageGatesAsync(featureId);
      await refreshRelease();
    } catch (e) {
      setMessagesError(toErrorMessage(e));
    } finally {
      gatesInFlight.current = false;
      setGatesRunning(false);
    }
  }, [api, featureId, refreshRelease]);

  const autoRunGates = useCallback(async (msgs: MessageDto[]) => {
    const latest = [...msgs].reverse().find((m) => m.role === "assistant" && m.bodyText);
    if (!latest || !latest.bodyText) return;
    if (latest.id === lastAutoRunMsgId.current) return;
    if (!hasStandaloneDone(latest.bodyText)) return;
    lastAutoRunMsgId.current = latest.id;
    await runGatesNow();
  }, [hasStandaloneDone, runGatesNow]);

  const loadMessages = useCallback(async () => {
    try {
      const msgs = await api.getStageMessagesAsync(featureId, stageRun.id);
      setMessages(msgs);
      void autoRunGates(msgs);
    } catch (e) {
      setMessagesError(toErrorMessage(e));
    }
  }, [api, featureId, stageRun.id, autoRunGates]);

  // Background sync for the chat transcript. Always enabled (not gated by stage status) —
  // the poller's own backoff already slows down once messages stop changing, so there's no
  // need to hand-maintain a separate on/off condition. maxConsecutiveErrors is left unset so
  // a backend restart is recovered from automatically instead of leaving the chat stuck.
  usePoller<MessageDto[]>({
    enabled: true,
    func: () => api.getStageMessagesAsync(featureId, stageRun.id),
    onResult: (msgs) => {
      setMessages(msgs);
      setMessagesError(null);
      void autoRunGates(msgs);
    },
    onError: (e) => setMessagesError(toErrorMessage(e)),
    pollIntervalMilliseconds: 2500,
    maxIntervalMilliseconds: 15000,
    deps: [featureId, stageRun.id],
  });

  useEffect(() => {
    if (stickToBottomRef.current) {
      messagesEndRef.current?.scrollIntoView?.({
        block: "nearest",
      });
    }
  }, [messages]);

  const handleSend = useCallback(async () => {
    const text = input.trim();
    if (!text || sending) return;
    setInput("");
    setMessages((prev) => [
      ...prev,
      { id: `pending-${Date.now()}`, role: "user", bodyText: text, createdAt: new Date().toISOString(), parts: [], isPriming: false },
    ]);
    setSending(true);
    setMessagesError(null);
    try {
      await api.sendStageMessageAsync(featureId, text);
      await loadMessages();
      // The server invalidates readyToProceed the moment a new message is sent — refresh
      // the release so a shown stage-complete card disappears immediately, not on the next poll.
      await refreshRelease();
    } catch (e) {
      setMessagesError(toErrorMessage(e));
    } finally {
      setSending(false);
    }
  }, [api, featureId, input, sending, loadMessages, refreshRelease]);

  const nextStepLabel = moveOnButtonLabel(nextStageName);

  return (
    <ZestResponsiveLayout
      className={styles.chatPanel}
      sidePaneWidth="480px"
      sidePane={{
        visible: inspecting !== null,
        title: "Injected prompt",
        content: <RichText text={inspecting?.bodyText} />,
        onClose: () => setInspecting(null),
      }}
    >
      {messagesError && <div className={styles.error}>{messagesError}</div>}
      <div
        className={styles.chatContainer}
        ref={chatContainerRef}
        onScroll={handleChatScroll}
        data-testid={`${testIdPrefix}-chat-messages`}
      >
        {messages.length === 0 && (
          <div className={styles.chatIntro}>
            Conversation with the {stageRun.stageName} agent will appear here.
            {stageRun.status === "BlockedSignoff" && " Gates are done — awaiting signoff above."}
          </div>
        )}
        {messages.map((message) => (
          <MessageRow key={message.id} message={message} onInspect={setInspecting} />
        ))}
        {sending && (
          <div className={styles.thinkingRow} role="status" data-testid={`${testIdPrefix}-thinking`}>
            <div className={styles.thinkingBubble}>thinking…</div>
          </div>
        )}
        {gatesRunning && (
          <div className={styles.thinkingRow} role="status" data-testid={`${testIdPrefix}-gates`}>
            <div className={styles.thinkingBubble}>running gates…</div>
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>

      {!stageRun.readyToProceed && (
        <div className={styles.stageHandoff}>
          <ZestButton
            type="button"
            onClick={() => void runGatesNow()}
            disabled={sending || busy || gatesRunning}
            data-testid={`${testIdPrefix}-move-on-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, buttonStyle: "outline", visualOptions: { size: "sm" } }}
          >
            {gatesRunning ? "Checking…" : nextStepLabel}
          </ZestButton>
        </div>
      )}

      <div className={styles.chatInput}>
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") void handleSend(); }}
          placeholder="Message the agent…"
          className={styles.input}
          disabled={sending || busy || gatesRunning}
          data-testid={`${testIdPrefix}-chat-input`}
        />
        <ZestButton
          type="button"
          onClick={() => void handleSend()}
          disabled={sending || busy || gatesRunning || !input.trim()}
          data-testid={`${testIdPrefix}-send-btn`}
          zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
        >
          Send
        </ZestButton>
      </div>
    </ZestResponsiveLayout>
  );
}

// ─── countdown run button (retry-with-a-window-to-intervene) ──────────────
//
// Unlike a fresh stage's AutoRunNotice (nothing to intervene on, so it just runs),
// a failed autonomous stage genuinely might warrant the user pushing back instead
// of blindly retrying — so this gives them a visible countdown and an explicit
// "Don't Run" to opt out, auto-retrying only if they don't act in time.

const AUTO_RUN_SECONDS = 30;

interface ICountdownRunButtonProps {
  testIdPrefix: string;
  disabled: boolean;
  busy: boolean;
  runningLabel: string;
  countingLabel: (secondsLeft: number) => string;
  onRun: () => void;
}

function CountdownRunButton({ testIdPrefix, disabled, busy, runningLabel, countingLabel, onRun }: ICountdownRunButtonProps) {
  const [secondsLeft, setSecondsLeft] = useState(AUTO_RUN_SECONDS);
  const [cancelled, setCancelled] = useState(false);
  const onRunRef = useRef(onRun);
  onRunRef.current = onRun;

  useEffect(() => {
    if (cancelled || secondsLeft <= 0) return;
    const timer = window.setTimeout(() => setSecondsLeft((s) => s - 1), 1000);
    return () => window.clearTimeout(timer);
  }, [cancelled, secondsLeft]);

  useEffect(() => {
    if (!cancelled && secondsLeft <= 0) onRunRef.current();
  }, [cancelled, secondsLeft]);

  // The split-button structure (ZestButton's dropdownOptions) is always present, never
  // conditionally removed — going from a split button to a plain one mid-interaction unmounts
  // Radix's DropdownMenu.Root in the same render pass its own item-click-closes-menu handling
  // runs in, which hangs indefinitely in jsdom (confirmed via isolated repro). Once used,
  // "Don't Run" just becomes a disabled, inert menu item instead of disappearing.
  const dropdownOptions = useMemo(
    () => [{ key: "dont-run", label: "Don't Run", disabled: cancelled, onClick: () => setCancelled(true) }],
    [cancelled],
  );

  return (
    <ZestButton
      type="button"
      onClick={onRun}
      disabled={disabled}
      data-testid={`${testIdPrefix}-run-stage-btn`}
      zest={{
        semanticType: "submit",
        busyOptions: { preventRageClick: true },
        visualOptions: { size: "sm" },
        dropdownAriaLabel: "More run options",
        dropdownOptions,
      }}
    >
      {busy ? "Running…" : cancelled ? runningLabel : countingLabel(secondsLeft)}
    </ZestButton>
  );
}

// ─── autonomous stage log ──────────────────────────────────────────────────

interface IStageLogProps {
  release: ReleaseDto;
  stageRun: ReleaseStageRunDto;
  api: BrokerApi;
  testIdPrefix: string;
  busy: boolean;
  runStage: () => void;
  refreshRelease: () => Promise<void>;
}

function StageLog({ stageRun, api, testIdPrefix, busy, runStage, refreshRelease }: IStageLogProps) {
  const logContainerRef = useRef<HTMLDivElement>(null);
  const logEndRef = useRef<HTMLDivElement>(null);
  // Only auto-follow new log lines while the reader is already at (or near) the bottom —
  // otherwise scrolling up to review earlier output keeps getting yanked back down.
  const stickToBottomRef = useRef(true);

  const handleLogScroll = useCallback(() => {
    const el = logContainerRef.current;
    if (!el) return;
    stickToBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < 48;
  }, []);

  // Without this, the stage log only reflects DB fields set once at the start/end of an
  // attempt, so a genuinely-running multi-minute turn looks identical to one silently
  // queued behind another release's turn (the broker only runs one turn at a time —
  // BrokerCoordinator's _turnLock). Polling /api/turns/current (already used by the
  // global ActiveTurnIndicator) and correlating on acpSessionId resolves that ambiguity.
  const [turn, setTurn] = useState<ActiveTurnInfo | undefined>(undefined);
  usePoller<ActiveTurnInfo | undefined>({
    enabled: true,
    func: () => api.getCurrentTurnAsync(),
    onResult: (t) => setTurn(t),
    pollIntervalMilliseconds: 2000,
    maxIntervalMilliseconds: 10000,
    deps: [stageRun.id],
  });

  const [, forceTick] = useState(0);
  useEffect(() => {
    if (!turn) return;
    const timer = window.setInterval(() => forceTick((t) => t + 1), 1000);
    return () => window.clearInterval(timer);
  }, [turn]);

  const isRunningHere = turn !== undefined && turn.acpSessionId === stageRun.acpSessionId;
  const isWaitingOnOtherTurn = turn !== undefined && turn.acpSessionId !== stageRun.acpSessionId;
  // The backend now persists exactly why a turn didn't complete (Escalated + lastErrorKind)
  // instead of this having to be guessed client-side from "no active turn right now" — that
  // heuristic stays as a fallback for the brief window before the backend's own error state
  // has been polled in, not as the primary signal.
  const looksStalled = turn === undefined && stageRun.status === "Active";
  const inProgress = !["BlockedGate", "BlockedEntry", "BlockedSignoff", "Complete", "Escalated"].includes(stageRun.status);

  const lines = useMemo(() => {
    const out: { text: string; tone: "phase" | "ok" | "warn" | "err" | "plain" }[] = [
      { text: `attempt ${stageRun.attempt} started — ${phaseLabel(stageRun.phase)}`, tone: "phase" },
    ];

    for (const note of stageRun.guidanceNotes ?? []) {
      out.push({ text: `rework note: ${note.text}`, tone: "warn" });
    }

    for (const gc of stageRun.gateChecks) {
      out.push({
        text: `${gc.passed ? "✓" : "✗"} gate ${gc.name}${gc.evidenceText ? ` — ${gc.evidenceText}` : ""}`,
        tone: gc.passed ? "ok" : "err",
      });
    }

    for (const f of stageRun.findings) {
      out.push({ text: `⚠ ${f.severity} ${f.target}: ${f.summary}`, tone: f.severity === "Blocker" ? "err" : "warn" });
    }

    if (stageRun.status === "Escalated") {
      const reason = errorKindLabel(stageRun.lastErrorKind) || "The agent hit an error and needs a retry.";
      out.push({ text: `⚠ ${reason}`, tone: "err" });
    } else if (stageRun.status === "BlockedGate") {
      out.push({ text: "stage blocked — gates failed. Review findings and push back for rework.", tone: "err" });
    } else if (stageRun.status === "BlockedEntry") {
      out.push({ text: "stage blocked before it could start — an entry check failed (often something the previous stage needs to fix).", tone: "err" });
    } else if (stageRun.status === "BlockedSignoff") {
      out.push({ text: "stage blocked — signoff required to continue.", tone: "warn" });
    } else if (stageRun.status === "Complete") {
      out.push({ text: "stage complete.", tone: "ok" });
    } else {
      out.push({ text: `${phaseLabel(stageRun.phase)}…`, tone: "phase" });
    }
    return out;
  }, [stageRun]);

  useEffect(() => {
    if (stickToBottomRef.current) {
      logEndRef.current?.scrollIntoView?.({
        block: "nearest",
      });
    }
  }, [lines]);

  const showRunButton = shouldPollStage(stageRun.status) || stageRun.status === "BlockedGate" || stageRun.status === "BlockedEntry" || stageRun.status === "Escalated";
  const showRefreshButton = shouldPollStage(stageRun.status);

  return (
    <div className={styles.logPanel}>
      <div className={styles.logContainer} data-testid={`${testIdPrefix}-stage-log`} ref={logContainerRef} onScroll={handleLogScroll}>
        {lines.map((line, i) => (
          <div
            key={i}
            className={`${styles.logLine} ${line.tone === "phase" ? styles.logPhase : line.tone === "ok" ? styles.logLineOk : line.tone === "err" ? styles.logLineBad : line.tone === "warn" ? styles.logLineWarn : ""}`}
          >
            {line.text}
          </div>
        ))}
        {inProgress && isRunningHere && turn && (
          <div className={`${styles.logLine} ${styles.logPhase} ${styles.logLiveStatus}`}>
            <FaSpinner className={styles.spinIcon} aria-hidden="true" />
            <span>running — {formatElapsed(turn.startedAt)}</span>
          </div>
        )}
        {inProgress && isWaitingOnOtherTurn && (
          <div className={`${styles.logLine} ${styles.logLineWarn}`}>
            waiting — the broker is busy with another release right now
          </div>
        )}
        {inProgress && looksStalled && (
          <div className={`${styles.logLine} ${styles.logLineWarn}`}>
            No active turn detected — this stage may need a retry.
          </div>
        )}
        <div ref={logEndRef} />
      </div>

      <div className={styles.logActions}>
        {showRunButton && (stageRun.status === "BlockedGate" || stageRun.status === "BlockedEntry" || stageRun.status === "Escalated") && (
          <CountdownRunButton
            testIdPrefix={testIdPrefix}
            disabled={busy}
            busy={busy}
            runningLabel="Run Stage Again"
            countingLabel={(s) => `Running again in ${s} second${s === 1 ? "" : "s"}. Click To Run Now.`}
            onRun={runStage}
          />
        )}
        {showRunButton && stageRun.status !== "BlockedGate" && stageRun.status !== "BlockedEntry" && stageRun.status !== "Escalated" && (
          <ZestButton
            type="button"
            onClick={runStage}
            disabled={busy}
            data-testid={`${testIdPrefix}-run-stage-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
          >
            {busy ? "Running…" : shouldPollStage(stageRun.status) ? "Run Stage Again" : "Run Stage"}
          </ZestButton>
        )}
        {showRefreshButton && (
          <ZestButton
            type="button"
            onClick={() => void refreshRelease()}
            disabled={busy}
            data-testid={`${testIdPrefix}-refresh-log-btn`}
            zest={{ semanticType: "refresh", busyOptions: { preventRageClick: true }, buttonStyle: "text", visualOptions: { size: "sm" } }}
          >
            Refresh
          </ZestButton>
        )}
      </div>
    </div>
  );
}

// ─── push-back panel ───────────────────────────────────────────────────────

interface IPushBackPanelProps {
  featureId: string;
  api: BrokerApi;
  stageIndex: number;
  pipeline: PipelineStageDto[];
  stageRun: ReleaseStageRunDto;
  testIdPrefix: string;
  refreshRelease: () => Promise<void>;
}

function PushBackPanel({ featureId, api, stageIndex, pipeline, stageRun, testIdPrefix, refreshRelease }: IPushBackPanelProps) {
  const [target, setTarget] = useState(pipeline[Math.max(0, stageIndex - 1)]?.name ?? "");
  const draft = useMemo(() => draftPushBackInstructions(stageRun), [stageRun]);
  const [instructions, setInstructions] = useState(draft);
  const lastDraftRef = useRef(draft);
  const [pushing, setPushing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const targets = pipeline.slice(0, stageIndex).map((p) => p.name);

  useEffect(() => {
    if (!target && targets.length > 0) setTarget(targets[targets.length - 1]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Re-draft when the underlying failure evidence actually changes (a fresh retry produced
  // new gate/finding results) — but only if the user hasn't already edited the previous draft,
  // so we never silently overwrite something they've customized.
  useEffect(() => {
    if (draft === lastDraftRef.current) return;
    setInstructions((current) => (current === lastDraftRef.current ? draft : current));
    lastDraftRef.current = draft;
  }, [draft]);

  const handlePushBack = useCallback(async () => {
    if (!target || !instructions.trim()) return;
    setPushing(true);
    setError(null);
    try {
      await api.pushBackAsync(featureId, target, instructions.trim());
      setInstructions("");
      await refreshRelease();
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setPushing(false);
    }
  }, [api, featureId, target, instructions, refreshRelease]);

  if (targets.length === 0) return null;

  return (
    <div className={styles.pushBack}>
      <div className={styles.pushBackBanner}>
        This stage&apos;s gates failed — the fix needs to happen in an earlier stage. We&apos;ve
        drafted rework instructions below from the failure details; review them, then click{" "}
        <strong>Push Back</strong> to send it back for correction.
      </div>
      {error && <div className={styles.error}>{error}</div>}
      <div className={styles.pushBackTitle}>Push back to an earlier stage for rework</div>
      <label className={styles.pushBackLabel}>
        Target stage
        <select
          value={target}
          onChange={(e) => setTarget(e.target.value)}
          className={styles.pushBackSelect}
          data-testid={`${testIdPrefix}-pushback-target`}
        >
          {targets.map((name) => (
            <option key={name} value={name}>{name}</option>
          ))}
        </select>
      </label>
      <label className={styles.pushBackLabel}>
        Instructions for the rework
        <textarea
          value={instructions}
          onChange={(e) => setInstructions(e.target.value)}
          placeholder="e.g. The login form is missing client-side validation. Add it and re-run the tests."
          className={styles.pushBackTextarea}
          data-testid={`${testIdPrefix}-pushback-instructions`}
        />
      </label>
      <ZestButton
        type="button"
        onClick={() => void handlePushBack()}
        disabled={pushing || !target || !instructions.trim()}
        data-testid={`${testIdPrefix}-pushback-btn`}
        zest={{ visualOptions: { variant: "danger", size: "sm" }, busyOptions: { preventRageClick: true } }}
      >
        {pushing ? "Pushing back…" : "Push Back"}
      </ZestButton>
    </div>
  );
}

// Shared by the full stage-complete card and the always-visible approve control below it —
// both need to trigger the same "approve every pending signoff for this stage" action.
function useApproveSignoff(
  featureId: string,
  pendingSignoffs: ReleaseSignoffDto[],
  api: BrokerApi,
  onReleaseUpdated: (release: ReleaseDto) => void,
) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const approve = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      let updated: ReleaseDto | null = null;
      for (const s of pendingSignoffs) {
        updated = await api.signoffFeatureAsync(featureId, s.stageName, "user", "Approved via wizard");
      }
      if (updated) onReleaseUpdated(updated);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, featureId, pendingSignoffs, onReleaseUpdated]);

  return { approve, loading, error };
}

// ─── stage-complete card: artifacts produced + explicit proceed ────────────

interface IStageCompleteCardProps {
  featureId: string;
  stageRun: ReleaseStageRunDto;
  pendingSignoffs: ReleaseSignoffDto[];
  api: BrokerApi;
  testIdPrefix: string;
  nextStageName: string | null;
  onReleaseUpdated: (release: ReleaseDto) => void;
  onContinue: () => void;
}

function StageCompleteCard({ featureId, stageRun, pendingSignoffs, api, testIdPrefix, nextStageName, onReleaseUpdated, onContinue }: IStageCompleteCardProps) {
  const [artifacts, setArtifacts] = useState<StageArtifactDto[]>([]);
  const [artifactsError, setArtifactsError] = useState<string | null>(null);
  const { approve: handleProceed, loading, error } = useApproveSignoff(featureId, pendingSignoffs, api, onReleaseUpdated);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const result = await api.getStageArtifactsAsync(featureId, stageRun.id);
        if (!cancelled) setArtifacts(result);
      } catch (e) {
        if (!cancelled) setArtifactsError(toErrorMessage(e));
      }
    })();
    return () => { cancelled = true; };
  }, [api, featureId, stageRun.id]);

  return (
    <div className={styles.signoffSection} data-testid={`${testIdPrefix}-stage-complete-card`}>
      <h3>{stageLabel(stageRun.stageName)} stage complete</h3>
      <p className={styles.signoffIntro}>Review what was produced, then proceed to the next stage.</p>
      {artifactsError && <div className={styles.error}>{artifactsError}</div>}
      {artifacts.length > 0 && (
        <div className={styles.timeline}>
          {artifacts.map((a) => (
            <details key={a.relativePath} className={styles.advancedDetails}>
              <summary>{a.relativePath}</summary>
              {a.content != null ? (
                <pre className={styles.detailInfo}>{a.content}</pre>
              ) : (
                <div className={styles.stageSummary}>(directory — not shown)</div>
              )}
            </details>
          ))}
        </div>
      )}
      {error && <div className={styles.error}>{error}</div>}
      <div className={styles.stageHandoff}>
        <ZestButton
          type="button"
          onClick={onContinue}
          disabled={loading}
          data-testid={`${testIdPrefix}-continue-btn`}
          zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
        >
          Continue {stageLabel(stageRun.stageName)}
        </ZestButton>
        <ZestButton
          type="button"
          onClick={() => void handleProceed()}
          disabled={loading}
          data-testid={`${testIdPrefix}-proceed-btn`}
          zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
        >
          {loading ? "Proceeding…" : proceedButtonLabel(nextStageName)}
        </ZestButton>
      </div>
    </div>
  );
}

// ─── stage history card ────────────────────────────────────────────────────

interface IStageHistoryCardProps {
  stageRun: ReleaseStageRunDto;
  testIdPrefix: string;
}

function StageHistoryCard({ stageRun, testIdPrefix }: IStageHistoryCardProps) {
  return (
    <div className={styles.stageCard} data-testid={`${testIdPrefix}-stage-${stageRun.stageName}`}>
      <div className={styles.stageHeader}>
        <span className={styles.stageName}>{stageLabel(stageRun.stageName)}</span>
        <span className={`${styles.releaseStatus} ${statusColor(stageRun.status)}`}>{statusLabel(stageRun.status)}</span>
        <span className={styles.phaseBadge}>{phaseLabel(stageRun.phase)}</span>
        {stageRun.questionCount > 0 && (
          <span className={styles.questionBadge}>{stageRun.questionCount} messages</span>
        )}
        <span className={styles.questionBadge}>attempt {stageRun.attempt}</span>
      </div>
      {stageRun.summary && <div className={styles.stageSummary}>{stageRun.summary}</div>}
      {stageRun.gateChecks.length > 0 && (
        <div className={styles.gateChecks}>
          {stageRun.gateChecks.map((gc) => (
            <div key={gc.id} className={styles.gateCheck}>
              <span className={gc.passed ? styles.gatePassed : styles.gateFailed}>
                {gc.passed ? "✓" : "✗"}
              </span>
              <span className={styles.gateName}>{gc.name}</span>
              {gc.evidenceText && (
                <span className={styles.gateEvidence}>{gc.evidenceText}</span>
              )}
            </div>
          ))}
        </div>
      )}
      {stageRun.findings.length > 0 && (
        <div className={styles.findingsSection}>
          <h4>Findings</h4>
          {stageRun.findings.map((f) => (
            <FindingCard key={f.id} finding={f} />
          ))}
        </div>
      )}
    </div>
  );
}

// ─── finding card ──────────────────────────────────────────────────────────

function FindingCard({ finding }: { finding: ReviewFindingDto }) {
  return (
    <div className={styles.findingCard}>
      <span className={`${styles.findingSeverity} ${finding.severity === "Blocker" ? styles.severityBlocker : finding.severity === "Major" ? styles.severityMajor : ""}`}>
        {finding.severity}
      </span>
      <span className={styles.findingTarget}>{finding.target}</span>
      <span className={styles.findingSummary}>{finding.summary}</span>
    </div>
  );
}