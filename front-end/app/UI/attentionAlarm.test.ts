import { decideChime, REMINDER_MS } from "./attentionAlarm";

describe("decideChime", () => {
  const none = new Set<string>();

  it("stays silent when nothing is waiting", () => {
    expect(decideChime(none, [], null, 1000)).toBe(false);
    expect(decideChime(new Set(["a"]), [], 0, 10 * REMINDER_MS)).toBe(false);
  });

  it("sounds straight away for a request that just appeared", () => {
    expect(decideChime(none, ["a"], null, 1000)).toBe(true);
  });

  it("sounds again when a second request joins the first", () => {
    expect(decideChime(new Set(["a"]), ["a", "b"], 1000, 1500)).toBe(true);
  });

  it("does not repeat on every poll of the same unanswered request", () => {
    expect(decideChime(new Set(["a"]), ["a"], 1000, 1000 + 4000)).toBe(false);
  });

  it("reminds once the reminder interval has passed", () => {
    expect(decideChime(new Set(["a"]), ["a"], 1000, 1000 + REMINDER_MS)).toBe(true);
    expect(decideChime(new Set(["a"]), ["a"], 1000, 1000 + REMINDER_MS - 1)).toBe(false);
  });

  it("never reminds for something it has not sounded for yet", () => {
    expect(decideChime(new Set(["a"]), ["a"], null, 10 * REMINDER_MS)).toBe(false);
  });
});
