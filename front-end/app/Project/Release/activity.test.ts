import { TurnActivityEntryDto } from "../../Chat/Data/BrokerTypes";
import {
  activityKindLabel,
  feedAge,
  isQuiet,
  quietForMs,
  quietMessage,
  visibleActivity,
} from "./activity";

const NOW = Date.parse("2026-01-10T12:00:00Z");

function entry(o: Partial<TurnActivityEntryDto> = {}): TurnActivityEntryDto {
  return { at: "2026-01-10T11:59:50Z", kind: "tool", label: "Read foo.cs", ...o };
}

describe("activityKindLabel", () => {
  it("names each kind without jargon", () => {
    expect(activityKindLabel("tool")).toBe("Tool");
    expect(activityKindLabel("text")).toBe("Said");
    expect(activityKindLabel("thought")).toBe("Thinking");
    expect(activityKindLabel("status")).toBe("Status");
    expect(activityKindLabel("something_new")).toBe("Activity");
  });
});

describe("visibleActivity", () => {
  const feed = [
    entry({ kind: "tool", label: "first" }),
    entry({ kind: "thought", label: "private reasoning" }),
    entry({ kind: "text", label: "latest" }),
  ];

  it("hides private thoughts by default", () => {
    expect(visibleActivity(feed, false).map((e) => e.label)).toEqual(["latest", "first"]);
  });

  it("includes thoughts when the user opts in", () => {
    expect(visibleActivity(feed, true).map((e) => e.label)).toEqual(["latest", "private reasoning", "first"]);
  });

  it("is empty when there is nothing to show", () => {
    expect(visibleActivity(null, true)).toEqual([]);
    expect(visibleActivity([], true)).toEqual([]);
  });
});

describe("quiet time", () => {
  it("measures how long the agent has been silent", () => {
    expect(quietForMs("2026-01-10T11:59:50Z", NOW)).toBe(10_000);
    expect(quietForMs(null, NOW)).toBeNull();
    expect(quietForMs("not-a-date", NOW)).toBeNull();
  });

  it("only calls it quiet after a generous threshold", () => {
    expect(isQuiet("2026-01-10T11:59:50Z", NOW)).toBe(false); // 10s
    expect(isQuiet("2026-01-10T11:58:00Z", NOW)).toBe(true); // 2m
    expect(isQuiet(null, NOW)).toBe(false);
  });

  it("explains the quiet spell and what to do", () => {
    expect(quietMessage("2026-01-10T11:59:50Z", NOW)).toBeNull();

    const message = quietMessage("2026-01-10T11:58:00Z", NOW)!;
    expect(message).toMatch(/quiet for 2 minutes/);
    expect(message).toMatch(/cancel and try again/);
  });

  it("says it looks stuck when the agent never produced anything at all", () => {
    // The distinction matters: silence after real work may just be hard thinking, but silence
    // from the very first second is a hang — and must not be dressed up as progress.
    const message = quietMessage("2026-01-10T11:58:00Z", NOW, false)!;

    expect(message).toMatch(/hasn't produced anything for 2 minutes/);
    expect(message).toMatch(/looks stuck/);
    expect(message).not.toMatch(/working through something hard/);
  });

  it("uses the singular for one minute", () => {
    expect(quietMessage("2026-01-10T11:59:00Z", NOW)!).toMatch(/quiet for 1 minute\b/);
  });
});

describe("feedAge", () => {
  it("reads as elapsed time", () => {
    expect(feedAge("2026-01-10T11:59:59.500Z", NOW)).toBe("just now");
    expect(feedAge("2026-01-10T11:59:48Z", NOW)).toBe("12s ago");
    expect(feedAge("2026-01-10T11:56:00Z", NOW)).toBe("4m ago");
  });

  it("is empty without a timestamp", () => {
    expect(feedAge(null, NOW)).toBe("");
  });
});
