import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import {
  ReleaseDto,
  ReleaseGateCheckDto,
  ReleaseStageRunDto,
} from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/ReleaseWizard.module.css";

interface IReleaseWizardProps {
  api: BrokerApi;
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
    case "BlockedSignoff": return styles.statusBlocked;
    case "Ready": return styles.statusReady;
    default: return "";
  }
}

export default function ReleaseWizard({ api, testIdPrefix = "release" }: IReleaseWizardProps) {
  const [view, setView] = useState<WizardView>("list");
  const [releases, setReleases] = useState<ReleaseDto[]>([]);
  const [selectedRelease, setSelectedRelease] = useState<ReleaseDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [featureKey, setFeatureKey] = useState("");
  const [workspacePath, setWorkspacePath] = useState("");

  const loadReleases = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const list = await api.listReleasesAsync();
      setReleases(list);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api]);

  useEffect(() => {
    if (view === "list") loadReleases();
  }, [view, loadReleases]);

  const handleCreate = useCallback(async () => {
    if (!featureKey.trim() || !workspacePath.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const release = await api.createReleaseAsync(featureKey.trim(), workspacePath.trim());
      setSelectedRelease(release);
      setView("detail");
      setFeatureKey("");
      setWorkspacePath("");
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

  const handleAdvance = useCallback(async () => {
    if (!selectedRelease) return;
    setLoading(true);
    setError(null);
    try {
      const updated = await api.advanceReleaseAsync(selectedRelease.id);
      setSelectedRelease(updated);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, selectedRelease]);

  const handleSignoff = useCallback(async (stageName: string) => {
    if (!selectedRelease) return;
    setLoading(true);
    setError(null);
    try {
      const updated = await api.signoffReleaseAsync(selectedRelease.id, stageName, "user", "Approved via wizard");
      setSelectedRelease(updated);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, selectedRelease]);

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
            <button onClick={() => setView("create")} disabled={loading}>
              New Release
            </button>
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
                <span className={`${styles.releaseStatus} ${statusColor(r.status)}`}>{r.status}</span>
                <span className={styles.releaseStages}>
                  {r.flowPosition?.currentStageName ?? "—"}
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
            <button onClick={() => setView("list")} disabled={loading}>Back</button>
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
            <label>
              Workspace Path
              <input
                value={workspacePath}
                onChange={(e) => setWorkspacePath(e.target.value)}
                placeholder="e.g. C:\\work\\project"
                data-testid={`${testIdPrefix}-workspace-path`}
              />
            </label>
            <button
              onClick={handleCreate}
              disabled={loading || !featureKey.trim() || !workspacePath.trim()}
              data-testid={`${testIdPrefix}-create-btn`}
            >
              {loading ? "Creating..." : "Create Release"}
            </button>
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
          onAdvance={handleAdvance}
          onSignoff={handleSignoff}
          onRefresh={handleRefresh}
        />
      )}
    </div>
  );
}

interface IReleaseDetailProps {
  release: ReleaseDto;
  api: BrokerApi;
  testIdPrefix: string;
  loading: boolean;
  onBack: () => void;
  onAdvance: () => void;
  onSignoff: (stageName: string) => void;
  onRefresh: () => void;
}

function ReleaseDetail({ release, testIdPrefix, loading, onBack, onAdvance, onSignoff, onRefresh }: IReleaseDetailProps) {
  const pendingSignoffs = release.signoffs.filter((s) => s.required && !s.approved);
  const canAdvance = release.status !== "Ready" && !loading;

  return (
    <div className={styles.detailView}>
      <div className={styles.header}>
        <button onClick={onBack} disabled={loading}>Back</button>
        <h2>{release.title ?? release.features[0]?.key}</h2>
        <span className={`${styles.releaseStatus} ${statusColor(release.status)}`}>{release.status}</span>
        <button onClick={onRefresh} disabled={loading}>Refresh</button>
      </div>

      <div className={styles.detailInfo}>
        <span>ID: {release.id.slice(0, 8)}...</span>
        <span>Workspace: {release.workspacePath}</span>
        <span>Stage: {release.flowPosition?.currentStageName ?? "—"}</span>
      </div>

      <div className={styles.stagesSection}>
        <h3>Stages</h3>
        {release.stageRuns.length === 0 && <div className={styles.empty}>No stages run yet.</div>}
        {release.stageRuns.map((sr) => (
          <StageRunCard key={sr.id} stageRun={sr} testIdPrefix={testIdPrefix} />
        ))}
      </div>

      <div className={styles.actions}>
        {canAdvance && (
          <button onClick={onAdvance} data-testid={`${testIdPrefix}-advance-btn`}>
            Advance
          </button>
        )}
        {pendingSignoffs.map((s) => (
          <button
            key={s.stageName}
            onClick={() => onSignoff(s.stageName)}
            className={styles.signoffBtn}
            data-testid={`${testIdPrefix}-signoff-${s.stageName}`}
          >
            Approve {s.stageName}
          </button>
        ))}
      </div>
    </div>
  );
}

interface IStageRunCardProps {
  stageRun: ReleaseStageRunDto;
  testIdPrefix: string;
}

function StageRunCard({ stageRun, testIdPrefix }: IStageRunCardProps) {
  return (
    <div className={styles.stageCard} data-testid={`${testIdPrefix}-stage-${stageRun.stageName}`}>
      <div className={styles.stageHeader}>
        <span className={styles.stageName}>{stageRun.stageName}</span>
        <span className={`${styles.releaseStatus} ${statusColor(stageRun.status)}`}>{stageRun.status}</span>
      </div>
      {stageRun.summary && <div className={styles.stageSummary}>{stageRun.summary}</div>}
      {stageRun.gateChecks.length > 0 && (
        <div className={styles.gateChecks}>
          {stageRun.gateChecks.map((gc) => (
            <GateCheckRow key={gc.id} gateCheck={gc} />
          ))}
        </div>
      )}
    </div>
  );
}

interface IGateCheckRowProps {
  gateCheck: ReleaseGateCheckDto;
}

function GateCheckRow({ gateCheck }: IGateCheckRowProps) {
  return (
    <div className={styles.gateCheck}>
      <span className={gateCheck.passed ? styles.gatePassed : styles.gateFailed}>
        {gateCheck.passed ? "✓" : "✗"}
      </span>
      <span className={styles.gateName}>{gateCheck.name}</span>
      {gateCheck.evidenceText && (
        <span className={styles.gateEvidence}>{gateCheck.evidenceText}</span>
      )}
    </div>
  );
}
