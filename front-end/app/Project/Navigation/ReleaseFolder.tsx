import React from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto } from "../../Chat/Data/BrokerTypes";
import { ShipReleaseButton } from "../Release/ReleaseWizard";
import { effectiveReleaseStatus, attentionFor, featureLastActivity, featureProgress, folderSummary, relativeTime, sortFeatures } from "./releaseView";
import { featureStateLabel, releaseStateLabel } from "./terms";
import { buildRoute } from "./routes";
import NewFeatureTile from "./NewFeatureTile";
import styles from "../Styles/Navigation.module.css";

interface IReleaseFolderProps {
  api: BrokerApi;
  release: ReleaseDto;
  pipelineStageNames: string[];
  /** The feature being worked on anywhere in this project, if any (may belong to another release). */
  activeFeature: { key: string } | null;
  onReleaseUpdated: (release: ReleaseDto) => void;
  onFeatureCreated: (featureId: string) => void;
  /** Inject a clock so tests are deterministic. */
  now?: Date;
  testIdPrefix?: string;
}

/**
 * Level 1: every feature of a release as a card, an always-visible Add feature, and a ship
 * action only when every feature really is done (the reported bug: a stale stored "Ready").
 * A shipped or cancelled release is a read-only record — nothing here offers to change it.
 */
export default function ReleaseFolder({
  api,
  release,
  pipelineStageNames,
  activeFeature,
  onReleaseUpdated,
  onFeatureCreated,
  now = new Date(),
  testIdPrefix = "release",
}: IReleaseFolderProps) {
  const summary = folderSummary(release);
  const open = !["Released", "Cancelled"].includes(effectiveReleaseStatus(release));
  const everyDone = release.features.length > 0 && release.features.every((f) => f.status === "Complete");
  const sorted = sortFeatures(release.features);

  return (
    <div className={styles.folder} data-testid={`${testIdPrefix}-folder`}>
      <div className={styles.folderHeader}>
        <h2 className={styles.folderTitle}>{release.title ?? release.features[0]?.key ?? "Release"}</h2>
        <span className={styles.folderState} data-testid={`${testIdPrefix}-folder-state`}>
          {summary.stateLabel}
        </span>
        <span className={styles.folderSummary} data-testid={`${testIdPrefix}-folder-summary`}>
          {summary.totalCount === 0 ? "Empty" : `${summary.doneCount} of ${summary.totalCount} features done`}
        </span>
      </div>

      {release.features.length === 0 && (
        <div className={styles.folderEmpty} data-testid={`${testIdPrefix}-folder-empty`}>
          <h3 className={styles.folderEmptyHeading}>No features yet</h3>
          <p>This release doesn&apos;t have any features yet. Add one to begin the workflow.</p>
        </div>
      )}

      {open && (
        <NewFeatureTile
          api={api}
          release={release}
          activeFeature={activeFeature}
          onReleaseUpdated={onReleaseUpdated}
          onFeatureCreated={onFeatureCreated}
          testIdPrefix={testIdPrefix}
        />
      )}

      <ul className={styles.cardGrid}>
        {sorted.map((feature) => {
          const attention = attentionFor(feature);
          const progress = featureProgress(feature, pipelineStageNames);
          const isActive = release.currentFeatureId === feature.id;
          return (
            <li key={feature.id}>
              <a
                href={buildRoute({ kind: "feature", releaseId: release.id, featureId: feature.id })}
                className={styles.featureCard}
                data-testid={`${testIdPrefix}-feature-card-${feature.key}`}
              >
                <span className={styles.featureCardName}>{feature.key}</span>
                <span className={styles.featureCardMeta}>
                  {isActive && <span className={styles.featureActive}>Active</span>}
                  <span className={styles.featureState}>{featureStateLabel(feature.status)}</span>
                </span>
                {feature.status === "InProgress" && (
                  <span className={styles.featureCardProgress}>{progress.label}</span>
                )}
                {attention.level === "needs-you" && (
                  <span className={styles.attentionBadge} data-testid={`${testIdPrefix}-attention-badge-${feature.key}`}>
                    {attention.reason}
                  </span>
                )}
                <span className={styles.featureCardActivity}>{relativeTime(featureLastActivity(feature), now)}</span>
              </a>
            </li>
          );
        })}
      </ul>

      {open && everyDone && (
        <ShipReleaseButton release={release} api={api} testIdPrefix={testIdPrefix} onReleaseUpdated={onReleaseUpdated} />
      )}
    </div>
  );
}