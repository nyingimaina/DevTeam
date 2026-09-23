import { CodeContextStatusDto } from "../../Chat/Data/BrokerTypes";
import { CODE_CONTEXT_REFRESH_LABEL, codeContextStateLine, codeContextWarningLines } from "./codeContext";

function status(partial: Partial<CodeContextStatusDto>): CodeContextStatusDto {
  return { state: "None", warnings: [], ...partial };
}

describe("codeContextStateLine", () => {
  it("shows the exact plain wording for every state", () => {
    expect(codeContextStateLine(status({ state: "UpToDate" }))).toBe("Project overview: up to date");
    expect(codeContextStateLine(status({ state: "Behind" }))).toBe(
      "Project overview: a few changes behind — updating in the background",
    );
    expect(codeContextStateLine(status({ state: "Refreshing" }))).toBe("Project overview: updating…");
    expect(codeContextStateLine(status({ state: "Unavailable" }))).toBe(
      "Project overview: unavailable. A helper tool it needs isn't installed.",
    );
    expect(codeContextStateLine(status({ state: "None" }))).toBe(
      "Project overview: not created yet — it will be made after your first finished feature",
    );
  });

  it("treats a missing status as not created yet", () => {
    expect(codeContextStateLine(null)).toContain("not created yet");
    expect(codeContextStateLine(undefined)).toContain("not created yet");
  });
});

describe("codeContextWarningLines", () => {
  it("explains a too-large project in plain words", () => {
    expect(codeContextWarningLines(status({ warnings: ["pack-too-large"] }))).toEqual([
      "Your project is very large, so the overview was skipped.",
    ]);
  });

  it("explains an omitted secret-looking file without naming the file or the secret", () => {
    const lines = codeContextWarningLines(status({ warnings: ["secret-suspected:config/keys.json"] }));
    expect(lines).toEqual(["A file that may contain a password was left out of the overview."]);
    expect(lines.join(" ")).not.toContain("keys.json");
  });

  it("deduplicates repeated warnings", () => {
    const lines = codeContextWarningLines(status({ warnings: ["repomix-failed", "repomix-failed"] }));
    expect(lines).toEqual(["The overview couldn't be updated this time — it will try again later."]);
  });
});

describe("plain-language wording", () => {
  it("never shows internal jargon", () => {
    const all = [
      CODE_CONTEXT_REFRESH_LABEL,
      ...(["UpToDate", "Behind", "Refreshing", "Unavailable", "None"] as const).map((state) =>
        codeContextStateLine(status({ state })),
      ),
      ...codeContextWarningLines(status({ warnings: ["pack-too-large", "secret-suspected:a", "repomix-missing", "repomix-failed"] })),
    ].join(" ");

    expect(all).not.toMatch(/repomix|pack|xml|commit|HEAD|stale|index|token|secretlint|git\b|branch/i);
  });
});
