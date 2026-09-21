import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";

// Pure view-model helpers for the project → release → feature navigation. No React, no I/O:
// everything here is derived from the polled release payload each render, never stored.

/**
 * "Ready" means every feature is complete. The stored release status is set whenever a feature
 * completes and never reset when another is added, so it goes stale — the broker now sends a
 * derived `effectiveStatus`; the fallback keeps this correct against an older broker.
 */
export function effectiveReleaseStatus(release: ReleaseDto): string {
  if (release.effectiveStatus) return release.effectiveStatus;
  if (release.status === "Ready" && (release.features.length === 0 || release.features.some((f) => f.status !== "Complete"))) {
    return "InProgress";
  }
  return release.status;
}

/**
 * The release-level `stageRuns` / `signoffs` / `flowPosition` are proxies of the *current*
 * (checked-out) feature only, so the existing stage screens can only ever show that one. This
 * returns a copy whose proxies point at any chosen feature, so they can show it instead.
 * Pure: never mutates its input.
 */
export function releaseForFeature(release: ReleaseDto, featureId: string): ReleaseDto {
  const feature = release.features.find((f) => f.id === featureId);
  if (!feature) return release;

  const status = effectiveReleaseStatus(release);
  return {
    ...release,
    currentFeatureId: feature.id,
    stageRuns: feature.stageRuns ?? [],
    signoffs: feature.signoffs ?? [],
    flowPosition: feature.flowPosition ?? null,
    // A feature is never "release complete" — that message belongs to the release folder.
    status: status === "Ready" ? "InProgress" : status,
  };
}

export interface Attention {
  level: "none" | "needs-you";
  reason?: string;
}

const NONE: Attention = { level: "none" };

function latestRun(runs: ReleaseStageRunDto[]): ReleaseStageRunDto | undefined {
  return [...runs].sort((a, b) => b.attempt - a.attempt || (b.startedAt ?? "").localeCompare(a.startedAt ?? ""))[0];
}

/** Whether a feature is waiting on the user, and why — in plain words. */
export function attentionFor(feature: ReleaseFeatureDto): Attention {
  if (feature.status === "Complete") return NONE;

  const run = latestRun(feature.stageRuns ?? []);
  if (!run) return NONE;

  switch (run.status) {
    case "BlockedSignoff":
      return { level: "needs-you", reason: "Needs your approval" };
    case "BlockedGate":
      return { level: "needs-you", reason: "Some checks didn't pass" };
    case "BlockedEntry":
      return { level: "needs-you", reason: "The previous step needs attention" };
    case "Escalated":
      return {
        level: "needs-you",
        reason: run.lastErrorKind === "ProviderRejected" ? "The AI service needs attention" : "The AI agent needs a retry",
      };
  }

  const waitingOnApproval = run.readyToProceed
    && (feature.signoffs ?? []).some((s) => s.required && !s.approved && s.stageName === run.stageName);
  return waitingOnApproval ? { level: "needs-you", reason: "Needs your approval" } : NONE;
}
