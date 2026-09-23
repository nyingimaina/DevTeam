import React, { useCallback, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import { toErrorMessage } from "../Release/ReleaseWizard";
import styles from "../Styles/Navigation.module.css";

interface INewFeatureTileProps {
  api: BrokerApi;
  release: ReleaseDto;
  /** The feature being worked on anywhere in this project; if set, starting this one pauses it. */
  activeFeature: { key: string } | null;
  onReleaseUpdated: (release: ReleaseDto) => void;
  onFeatureCreated: (featureId: string) => void;
  testIdPrefix?: string;
}

/**
 * The always-visible "＋ New feature" tile (REQ-003/REQ-004). Asking for a feature is honest
 * about its one consequence, in plain words, before anything is created: the currently active
 * feature of the project (if any) will be paused and can be resumed later.
 */
export default function NewFeatureTile({ api, release, activeFeature, onReleaseUpdated, onFeatureCreated, testIdPrefix = "release" }: INewFeatureTileProps) {
  const [open, setOpen] = useState(false);
  const [key, setKey] = useState("");
  const [creating, setCreating] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const reset = useCallback(() => {
    setOpen(false);
    setKey("");
    setConfirming(false);
    setError(null);
  }, []);

  const create = useCallback(async () => {
    setCreating(true);
    setError(null);
    try {
      const created = await api.createFeatureAsync(release.id, key.trim());
      const fresh = await api.getReleaseAsync(release.id);
      onReleaseUpdated(fresh);
      onFeatureCreated(created.id);
      setCreating(false);
      reset();
    } catch (e) {
      setCreating(false);
      setError(`We couldn't create that feature. ${toErrorMessage(e)}`);
    }
  }, [api, release.id, key, onReleaseUpdated, onFeatureCreated, reset]);

  const handleCreateClick = useCallback(() => {
    const name = key.trim();
    if (!name) return;
    setError(null);
    const alreadyUsed = release.features.some((f) => f.key.toLowerCase() === name.toLowerCase());
    if (alreadyUsed) {
      setError(`There's already a feature called "${name}" in this release.`);
      return;
    }
    if (!confirming && activeFeature) {
      setConfirming(true);
      return;
    }
    void create();
  }, [key, release.features, confirming, activeFeature, create]);

  if (!open) {
    return (
      <div className={styles.newTile}>
        <button
          type="button"
          className={styles.newTileButton}
          onClick={() => setOpen(true)}
          data-testid={`${testIdPrefix}-new-feature-open`}
        >
          ＋ New feature
        </button>
      </div>
    );
  }

  return (
    <div className={styles.newTile} data-testid={`${testIdPrefix}-new-feature-tile`}>
      <div className={styles.newTileFields}>
        <label htmlFor={`${testIdPrefix}-new-feature-key`}>Feature name</label>
        <input
          id={`${testIdPrefix}-new-feature-key`}
          className={styles.newTileInput}
          value={key}
          onChange={(e) => setKey(e.target.value)}
          placeholder="e.g. subtraction"
          data-testid={`${testIdPrefix}-new-feature-key`}
          autoFocus
        />
      </div>

      {activeFeature && (
        <p className={styles.newTileWarning} data-testid={`${testIdPrefix}-new-feature-warning`}>
          &ldquo;{key.trim() || "New feature"}&rdquo; will become your active feature. &ldquo;{activeFeature.key}&rdquo; will
          be paused and can be resumed later.
        </p>
      )}

      {error && (
        <p className={styles.errorInline} role="alert">{error}</p>
      )}

      <div className={styles.newTileActions}>
        {confirming ? (
          <ZestButton
            key="confirm"
            type="button"
            onClick={() => void handleCreateClick()}
            disabled={!key.trim() || creating}
            data-testid={`${testIdPrefix}-new-feature-confirm`}
            zest={{ semanticType: "save", busyOptions: { preventRageClick: true, minBusyDurationMs: 0 }, successOptions: { autoResetAfterMs: 0 }, visualOptions: { size: "sm" } }}
          >
            {creating ? "Creating…" : "Confirm"}
          </ZestButton>
        ) : (
          <ZestButton
            key="create"
            type="button"
            onClick={() => void handleCreateClick()}
            disabled={!key.trim() || creating}
            data-testid={`${testIdPrefix}-new-feature-create`}
            zest={{ semanticType: "save", busyOptions: { preventRageClick: true, minBusyDurationMs: 0 }, visualOptions: { size: "sm" } }}
          >
            {creating ? "Creating…" : "Create feature"}
          </ZestButton>
        )}
        <ZestButton
          type="button"
          onClick={reset}
          disabled={creating}
          data-testid={`${testIdPrefix}-new-feature-cancel`}
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}
        >
          Cancel
        </ZestButton>
      </div>
    </div>
  );
}