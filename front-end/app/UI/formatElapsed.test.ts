import { formatElapsed } from "./formatElapsed";

describe("formatElapsed", () => {
  it("renders seconds only under a minute", () => {
    const startedAt = new Date(Date.now() - 45_000).toISOString();
    expect(formatElapsed(startedAt)).toBe("45s");
  });

  it("renders minutes and seconds once a minute has passed", () => {
    const startedAt = new Date(Date.now() - 125_000).toISOString();
    expect(formatElapsed(startedAt)).toBe("2m5s");
  });

  it("never returns a negative duration for a clock-skewed future timestamp", () => {
    const startedAt = new Date(Date.now() + 5_000).toISOString();
    expect(formatElapsed(startedAt)).toBe("0s");
  });
});
