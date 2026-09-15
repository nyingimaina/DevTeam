import { formatCleanupNoticeMessage, projectNameFromPath } from "./workspaceCleanup";

describe("projectNameFromPath", () => {
  it("returns the last path segment for a Windows path", () => {
    expect(projectNameFromPath("D:\\apps\\tictactoe")).toBe("tictactoe");
  });

  it("returns the last path segment for a posix path", () => {
    expect(projectNameFromPath("/home/user/tictactoe")).toBe("tictactoe");
  });

  it("falls back to the whole path when there is no separator", () => {
    expect(projectNameFromPath("tictactoe")).toBe("tictactoe");
  });
});

describe("formatCleanupNoticeMessage", () => {
  it("returns null when nothing was stopped", () => {
    expect(formatCleanupNoticeMessage("D:\\apps\\tictactoe", [])).toBeNull();
  });

  it("formats a singular message for one stopped process", () => {
    const message = formatCleanupNoticeMessage("D:\\apps\\tictactoe", [{ processId: 1, name: "node.exe" }]);
    expect(message).toBe("Stopped 1 process from tictactoe: node.exe");
  });

  it("formats a plural message listing every stopped process by name", () => {
    const message = formatCleanupNoticeMessage("D:\\apps\\tictactoe", [
      { processId: 1, name: "node.exe" },
      { processId: 2, name: "GamePlay.Api.exe" },
    ]);
    expect(message).toBe("Stopped 2 processes from tictactoe: node.exe, GamePlay.Api.exe");
  });
});
