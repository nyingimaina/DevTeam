// The whole navigation state lives in the URL hash so a refresh, the back button and a pasted
// link all land in the same place. Hash routes (not /paths) because the app is a static export
// with a single page: they behave identically in `next dev`, production and tests.

export type Route =
  | { kind: "home" }
  | { kind: "release"; releaseId: string }
  | { kind: "feature"; releaseId: string; featureId: string }
  | { kind: "stage"; releaseId: string; featureId: string; stageName: string };

const HOME: Route = { kind: "home" };

// Ids are GUIDs; anything else that could be misread as a path or markup is rejected outright.
const SAFE_ID = /^[A-Za-z0-9_-]{1,64}$/;

export function buildRoute(route: Route): string {
  switch (route.kind) {
    case "home":
      return "#/";
    case "release":
      return `#/r/${route.releaseId}`;
    case "feature":
      return `#/r/${route.releaseId}/f/${route.featureId}`;
    case "stage":
      return `#/r/${route.releaseId}/f/${route.featureId}/s/${route.stageName}`;
  }
}

/** Never throws: anything that isn't a well-formed route is the project home. */
export function parseRoute(hash: string | null | undefined): Route {
  if (typeof hash !== "string") return HOME;

  const segments = hash.trim().replace(/^#/, "").split("/").filter((p) => p !== "");

  if (segments[0] !== "r" || segments.length < 2) return HOME;
  const releaseId = segments[1];
  if (!SAFE_ID.test(releaseId)) return HOME;
  if (segments.length === 2) return { kind: "release", releaseId };

  if (segments[2] !== "f" || segments.length < 4) return HOME;
  const featureId = segments[3];
  if (!SAFE_ID.test(featureId)) return HOME;
  if (segments.length === 4) return { kind: "feature", releaseId, featureId };

  if (segments[4] !== "s" || segments.length !== 6) return HOME;
  const stageName = segments[5];
  if (!SAFE_ID.test(stageName)) return HOME;
  return { kind: "stage", releaseId, featureId, stageName };
}
