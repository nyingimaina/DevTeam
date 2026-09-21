import { ReleaseDto, ReleaseFeatureDto, ReleaseStageRunDto } from "../../Chat/Data/BrokerTypes";
import { stageLabel } from "../Release/labels";
import { releaseStateLabel } from "./terms";

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

// ─── card / folder view-models ─────────────────────────────────────────────

export interface FeatureProgress {
  label: string;
  stageNumber: number;
  stageCount: number;
}

/** Where a feature is in the pipeline, in words: "Developer — stage 2 of 3", "Done", "Not started". */
export function featureProgress(feature: ReleaseFeatureDto, pipelineStageNames: string[]): FeatureProgress {
  const stageCount = pipelineStageNames.length;
  if (feature.status === "Complete") return { label: "Done", stageNumber: stageCount, stageCount };

  const current = feature.flowPosition?.currentStageName;
  if (!current) return { label: "Not started", stageNumber: 0, stageCount };

  const index = pipelineStageNames.indexOf(current);
  if (index < 0 || stageCount === 0) return { label: stageLabel(current), stageNumber: 0, stageCount };
  return { label: `${stageLabel(current)} — stage ${index + 1} of ${stageCount}`, stageNumber: index + 1, stageCount };
}

function groupRank(feature: ReleaseFeatureDto): number {
  if (feature.status === "Complete") return 4;
  if (attentionFor(feature).level === "needs-you") return 0;
  switch (feature.status) {
    case "InProgress": return 1;
    case "OnHold": return 2;
    case "Proposed": return 3;
    default: return 3;
  }
}

/** Needs-you first, then in progress, paused, not started, done; newest activity first within a group. */
export function sortFeatures(features: ReleaseFeatureDto[]): ReleaseFeatureDto[] {
  return [...features].sort((a, b) =>
    groupRank(a) - groupRank(b)
    || Date.parse(b.updatedAt) - Date.parse(a.updatedAt)
    || a.key.localeCompare(b.key));
}

export interface FolderSummary {
  doneCount: number;
  totalCount: number;
  stateLabel: string;
  needsYou: boolean;
  attentionReason?: string;
  /** ISO timestamp of the newest thing that happened in this release, if any. */
  lastActivity: string | null;
}

export function folderSummary(release: ReleaseDto): FolderSummary {
  const attention = release.features
    .map((f) => attentionFor(f))
    .find((a) => a.level === "needs-you");

  const stamps: string[] = [release.updatedAt];
  for (const f of release.features) {
    stamps.push(f.updatedAt);
    for (const r of f.stageRuns ?? []) {
      if (r.finishedAt) stamps.push(r.finishedAt);
      if (r.startedAt) stamps.push(r.startedAt);
    }
  }
  const valid = stamps.filter((s) => s && !Number.isNaN(Date.parse(s)));
  const lastActivity = valid.length === 0 ? null : valid.reduce((a, b) => (Date.parse(b) > Date.parse(a) ? b : a));

  return {
    doneCount: release.features.filter((f) => f.status === "Complete").length,
    totalCount: release.features.length,
    stateLabel: releaseStateLabel(effectiveReleaseStatus(release)),
    needsYou: attention !== undefined,
    attentionReason: attention?.reason,
    lastActivity,
  };
}

/** "5 minutes ago" — deterministic given `now`; empty string for a missing/invalid time. */
export function relativeTime(iso: string | null | undefined, now: Date = new Date()): string {
  if (!iso) return "";
  const then = Date.parse(iso);
  if (Number.isNaN(then)) return "";

  const seconds = Math.floor((now.getTime() - then) / 1000);
  if (seconds < 45) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${Math.max(minutes, 1)} minute${minutes === 1 ? "" : "s"} ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} hour${hours === 1 ? "" : "s"} ago`;
  const days = Math.floor(hours / 24);
  if (days < 30) return `${days} day${days === 1 ? "" : "s"} ago`;
  return new Date(then).toLocaleDateString("en-US", { year: "numeric", month: "short", day: "numeric" });
}
