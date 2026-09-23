import { clearErrors, installGlobalErrorHandlers, recentErrors, recordError } from "./diagnostics";

describe("diagnostics error ring", () => {
  beforeEach(() => {
    window.localStorage.clear();
    clearErrors();
  });

  it("starts empty", () => {
    expect(recentErrors()).toEqual([]);
  });

  it("records an error with a timestamp", () => {
    recordError({ source: "api", message: "boom", url: "GET /api/x", status: 500, requestId: "abc123" });

    const [entry] = recentErrors();
    expect(entry.message).toBe("boom");
    expect(entry.source).toBe("api");
    expect(entry.status).toBe(500);
    expect(entry.requestId).toBe("abc123");
    expect(Number.isNaN(Date.parse(entry.at))).toBe(false);
  });

  it("survives a reload, because a user often reloads before collecting anything", () => {
    recordError({ source: "window", message: "something broke" });

    // A fresh read of storage is what a reload would see.
    expect(recentErrors().map((e) => e.message)).toContain("something broke");
  });

  it("keeps only the most recent entries so it can never grow without bound", () => {
    for (let i = 0; i < 80; i += 1) {
      recordError({ source: "api", message: `failure ${i}` });
    }

    const entries = recentErrors();
    expect(entries).toHaveLength(50);
    expect(entries[entries.length - 1].message).toBe("failure 79");
    expect(entries[0].message).toBe("failure 30");
  });

  it("can be cleared", () => {
    recordError({ source: "api", message: "boom" });

    clearErrors();

    expect(recentErrors()).toEqual([]);
  });

  it("records window errors and unhandled rejections once handlers are installed", () => {
    installGlobalErrorHandlers();

    window.dispatchEvent(new ErrorEvent("error", { message: "render blew up" }));
    const rejection = new Event("unhandledrejection") as Event & { reason?: unknown };
    rejection.reason = new Error("promise blew up");
    window.dispatchEvent(rejection);

    const messages = recentErrors().map((e) => e.message);
    expect(messages).toContain("render blew up");
    expect(messages).toContain("promise blew up");
  });
});
