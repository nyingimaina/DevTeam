import React, { useCallback, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import { FaFire } from "react-icons/fa6";
import { toErrorMessage } from "../Release/ReleaseWizard";
import { effectiveReleaseStatus, folderSummary, relativeTime } from "./releaseView";
import { buildRoute } from "./routes";
import styles from "../Styles/Navigation.module.css";

interface IProjectHomeProps {
  api: BrokerApi;
  workspacePath: string;
  /** Every release and urgent fix for the project (open and shipped); the shipped ones are grouped. */
  releases: ReleaseDto[];
  loading: boolean;
  error: string | null;
  onRetry: () => void;
  /** Called with the freshly created release, so the shell can navigate straight into it. */
  onReleaseCreated: (release: ReleaseDto) => void;
  now?: Date;
  testIdPrefix?: string;
}

const CLOSED = new Set(["Released", "Cancelled"]);

function isOpen(release: ReleaseDto): boolean {
  return !CLOSED.has(effectiveReleaseStatus(release));
}

/** Needs-you folders first, then most recently active — deterministic (tie-break by id). */
function sortFolders(releases: ReleaseDto[]): ReleaseDto[] {
  return [...releases].sort((a, b) => {
    const needA = folderSummary(a).needsYou ? 0 : 1;
    const needB = folderSummary(b).needsYou ? 0 : 1;
    if (needA !== needB) return needA - needB;
    const recentA = folderSummary(a).lastActivity ?? "";
    const recentB = folderSummary(b).lastActivity ?? "";
    if (recentA !== recentB) return recentB.localeCompare(recentA);
    return a.id.localeCompare(b.id);
  });
}

/**
 * Level 0: every open release and urgent fix as a folder card, the create actions that start
 * them, and a collapsed "Shipped" section. Looking is free — each card is a real link.
 */
export default function ProjectHome({
  api,
  workspacePath,
  releases,
  loading,
  error,
  onRetry,
  onReleaseCreated,
  now = new Date(),
  testIdPrefix = "project",
}: IProjectHomeProps) {
  const [showReleaseForm, setShowReleaseForm] = useState(false);
  const [creatingRelease, setCreatingRelease] = useState(false);
  const [releaseKey, setReleaseKey] = useState("");
  const [showHotfixForm, setShowHotfixForm] = useState(false);
  const [creatingHotfix, setCreatingHotfix] = useState(false);
  const [hotfixKey, setHotfixKey] = useState("");
  const [createError, setCreateError] = useState<string | null>(null);
  const [showShipped, setShowShipped] = useState(false);

  const openFolders = sortFolders(releases.filter(isOpen));
  const shippedFolders = releases.filter((r) => !isOpen(r));
  const shippedText = `Shipped (${shippedFolders.length})`;

  const handleCreateRelease = useCallback(async () => {
    if (!releaseKey.trim()) return;
    setCreatingRelease(true);
    setCreateError(null);
    try {
      const created = await api.createReleaseAsync(releaseKey.trim(), workspacePath);
      setReleaseKey("");
      setShowReleaseForm(false);
      onReleaseCreated(created);
    } catch (e) {
      setCreateError(`We couldn't create that release. ${toErrorMessage(e)}`);
    } finally {
      setCreatingRelease(false);
    }
  }, [api, releaseKey, workspacePath, onReleaseCreated]);

  const handleCreateHotfix = useCallback(async () => {
    if (!hotfixKey.trim()) return;
    setCreatingHotfix(true);
    setCreateError(null);
    try {
      const started = await api.startHotfixAsync(hotfixKey.trim(), workspacePath);
      const fresh = await api.getReleaseAsync(started.releaseId);
      setHotfixKey("");
      setShowHotfixForm(false);
      onReleaseCreated(fresh);
    } catch (e) {
      setCreateError(`We couldn't create that urgent fix. ${toErrorMessage(e)}`);
    } finally {
      setCreatingHotfix(false);
    }
  }, [api, hotfixKey, workspacePath, onReleaseCreated]);

  const renderFolderCard = (release: ReleaseDto) => {
    const summary = folderSummary(release);
    const kind = release.isHotfix ? "Urgent fix" : "Release";
    return (
      <a
        key={release.id}
        href={buildRoute({ kind: "release", releaseId: release.id })}
        className={`${styles.homeFolder} ${release.isHotfix ? styles.homeFolderHotfix : ""}`}
        data-testid={`${testIdPrefix}-folder-${release.id}`}
      >
        <span className={styles.homeFolderKind}>
          {release.isHotfix ? <FaFire aria-hidden /> : null}
          {kind}
        </span>
        <span className={styles.homeFolderTitle}>{release.title ?? release.features[0]?.key ?? "Release"}</span>
        <span className={styles.homeFolderState}>{summary.stateLabel}</span>
        <span className={styles.homeFolderProgress}>
          {summary.totalCount === 0 ? "No features yet" : `${summary.doneCount} of ${summary.totalCount} features done`}
        </span>
        {summary.needsYou && summary.attentionReason && (
          <span className={styles.attentionBadge} data-testid={`${testIdPrefix}-attention-${release.id}`}>
            {summary.attentionReason}
          </span>
        )}
        <span className={styles.homeFolderActivity}>{relativeTime(summary.lastActivity, now)}</span>
      </a>
    );
  };

  return (
    <div className={styles.home} data-testid={`${testIdPrefix}-home`}>
      <div className={styles.homeHeader}>
        <h2 className={styles.homeTitle}>Project</h2>
        <div className={styles.homeActions}>
          {showReleaseForm ? (
            <div className={styles.homeCreateForm}>
              <input
                className={styles.newTileInput}
                value={releaseKey}
                onChange={(e) => setReleaseKey(e.target.value)}
                placeholder="e.g. login-form"
                data-testid={`${testIdPrefix}-release-key`}
              />
              <ZestButton
                type="button"
                onClick={() => void handleCreateRelease()}
                disabled={!releaseKey.trim() || creatingRelease}
                data-testid={`${testIdPrefix}-create-release`}
                zest={{ semanticType: "save", busyOptions: { preventRageClick: true }, successOptions: { autoResetAfterMs: 0 }, visualOptions: { size: "sm" } }}
              >
                {creatingRelease ? "Creating…" : "Create release"}
              </ZestButton>
            </div>
          ) : (
            <ZestButton
              type="button"
              onClick={() => setShowReleaseForm(true)}
              data-testid={`${testIdPrefix}-new-release-open`}
              zest={{ semanticType: "add", visualOptions: { size: "sm" } }}
            >
              ＋ New release
            </ZestButton>
          )}

          {showHotfixForm ? (
            <div className={styles.homeCreateForm}>
              <input
                className={styles.newTileInput}
                value={hotfixKey}
                onChange={(e) => setHotfixKey(e.target.value)}
                placeholder="e.g. critical-bug"
                data-testid={`${testIdPrefix}-hotfix-key`}
              />
              <ZestButton
                type="button"
                onClick={() => void handleCreateHotfix()}
                disabled={!hotfixKey.trim() || creatingHotfix}
                data-testid={`${testIdPrefix}-start-hotfix`}
                zest={{ semanticType: "save", busyOptions: { preventRageClick: true }, successOptions: { autoResetAfterMs: 0 }, visualOptions: { size: "sm" } }}
              >
                {creatingHotfix ? "Starting…" : "Start urgent fix"}
              </ZestButton>
            </div>
          ) : (
            <ZestButton
              type="button"
              onClick={() => setShowHotfixForm(true)}
              data-testid={`${testIdPrefix}-new-hotfix-open`}
              zest={{ semanticType: "add", visualOptions: { size: "sm" } }}
            >
              ＋ Urgent fix
            </ZestButton>
          )}
        </div>
      </div>

      {createError && <p className={styles.errorInline} role="alert">{createError}</p>}
      {error && (
        <div className={styles.homeError} role="alert" data-testid={`${testIdPrefix}-home-error`}>
          <span>{error}</span>
          <ZestButton
            type="button"
            onClick={onRetry}
            data-testid={`${testIdPrefix}-retry`}
            zest={{ buttonStyle: "text", busyOptions: { preventRageClick: true, minBusyDurationMs: 0 }, visualOptions: { size: "sm" } }}
          >
            Try again
          </ZestButton>
        </div>
      )}

      {loading ? (
        <div className={styles.homeLoading} data-testid={`${testIdPrefix}-home-loading`}>Loading…</div>
      ) : openFolders.length === 0 ? (
        <p className={styles.homeEmpty}>
          Start your first release — every finished feature then folds into the workflow below.
        </p>
      ) : (
        <ul className={styles.cardGrid}>{openFolders.map(renderFolderCard)}</ul>
      )}

      {!loading && shippedFolders.length > 0 && (
        <section className={styles.shipped} aria-label={shippedText}>
          <button
            type="button"
            className={styles.shippedToggle}
            onClick={() => setShowShipped((s) => !s)}
            aria-expanded={showShipped}
            data-testid={`${testIdPrefix}-shipped-toggle`}
          >
            {showShipped ? "▾" : "▸"} {shippedText}
          </button>
          {showShipped && <ul className={styles.cardGrid}>{shippedFolders.map(renderFolderCard)}</ul>}
        </section>
      )}
    </div>
  );
}