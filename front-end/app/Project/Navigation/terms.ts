// Every noun the navigation shows a person lives here, so the wording can change in one place.
// (Stage/phase wording stays in ../Release/labels.ts — this only adds the folder-level words.)

const FEATURE_STATES: Record<string, string> = {
  Complete: "Done",
  InProgress: "In progress",
  OnHold: "Paused",
  Proposed: "Not started",
};

const RELEASE_STATES: Record<string, string> = {
  Ready: "Ready to ship",
  Released: "Shipped",
  InProgress: "In progress",
  Draft: "Draft",
  Blocked: "Needs attention",
  Escalated: "Needs attention",
  Cancelled: "Cancelled",
};

export function featureStateLabel(status: string): string {
  return FEATURE_STATES[status] ?? status;
}

export function releaseStateLabel(status: string): string {
  return RELEASE_STATES[status] ?? status;
}

/** A hotfix is a release-shell branched from main; to a person it's an urgent fix. */
export function folderKindLabel(isHotfix: boolean): string {
  return isHotfix ? "Urgent fix" : "Release";
}
