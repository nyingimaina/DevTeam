import { CodeContextState, CodeContextStatusDto } from "../../Chat/Data/BrokerTypes";

/**
 * Every word the user reads about the project overview lives here, so it can be kept plain and
 * scanned for jargon in one place. The end user is non-technical: never surface the tool name or
 * anything about packs/commits (see REPOMIX_BRS §9).
 */
export const CODE_CONTEXT_REFRESH_LABEL = "Update overview now";

export function codeContextStateLine(status: CodeContextStatusDto | null | undefined): string {
  const state: CodeContextState = status?.state ?? "None";

  switch (state) {
    case "UpToDate":
      return "Project overview: up to date";
    case "Behind":
      return "Project overview: a few changes behind — updating in the background";
    case "Refreshing":
      return "Project overview: updating…";
    case "Unavailable":
      return "Project overview: unavailable. A helper tool it needs isn't installed.";
    case "None":
    default:
      return "Project overview: not created yet — it will be made after your first finished feature";
  }
}

export function codeContextWarningLines(status: CodeContextStatusDto | null | undefined): string[] {
  const warnings = status?.warnings ?? [];
  const lines: string[] = [];

  for (const warning of warnings) {
    if (warning === "pack-too-large") {
      lines.push("Your project is very large, so the overview was skipped.");
    } else if (warning.startsWith("secret-suspected")) {
      lines.push("A file that may contain a password was left out of the overview.");
    } else if (warning === "repomix-missing") {
      lines.push("A helper tool it needs isn't installed.");
    } else if (warning.startsWith("repomix-") || warning === "refresh-failed" || warning === "head-changed") {
      lines.push("The overview couldn't be updated this time — it will try again later.");
    }
  }

  return [...new Set(lines)];
}
