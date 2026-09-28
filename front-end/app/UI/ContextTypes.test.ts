import { contextFraction, type ContextDto } from "../Chat/Data/ContextTypes";

describe("contextFraction", () => {
  const defaults: ContextDto = {
    sessionId: "s1",
    usedTokens: 0,
    contextSize: 200_000,
    deltaTokens: null,
    costAmount: null,
    costCurrency: null,
    turnActive: false,
    compactionCount: 0,
    updatedAt: null,
  };

  const reading = (over: Partial<ContextDto>): ContextDto => ({ ...defaults, ...over });

  it("reports the share of the window that is occupied", () => {
    expect(contextFraction(reading({ usedTokens: 50_000 }))).toBeCloseTo(0.25);
  });

  it("returns nothing rather than zero when the window size is unknown", () => {
    // A bar drawn at 0% says the context was measured and found empty. "We do not know the window
    // size yet" is a different fact, and showing it as 0% would be a small lie on the first turn.
    expect(contextFraction(reading({ usedTokens: 40_000, contextSize: null }))).toBeNull();
  });

  it("returns nothing when there is no reading at all", () => {
    expect(contextFraction(null)).toBeNull();
    expect(contextFraction(undefined)).toBeNull();
  });

  it("treats a zero-sized window as unknown instead of as a full one", () => {
    expect(contextFraction(reading({ usedTokens: 40_000, contextSize: 0 }))).toBeNull();
  });

  it("does not report more than full when the agent overruns its stated window", () => {
    // Overrun happens in practice. A bar wider than its own track would be a rendering bug, and
    // hiding the overrun would be worse — so it saturates at 100% and the numbers still show the truth.
    expect(contextFraction(reading({ usedTokens: 260_000 }))).toBe(1);
  });

  it("reports a cleared context as genuinely empty", () => {
    // Distinct from unknown: a feature-boundary reset really does empty the context, and after one
    // of those, 0% is a measured fact.
    expect(contextFraction(reading({ usedTokens: 0, updatedAt: "2026-09-28T10:00:00Z" }))).toBe(0);
  });
});
