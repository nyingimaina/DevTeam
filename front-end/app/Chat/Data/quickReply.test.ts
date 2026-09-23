import { parseQuickReplyOptions } from "./quickReply";

describe("parseQuickReplyOptions", () => {
  it("parses a numbered list of 3 options at the end of the message", () => {
    const text = "Which numeric model should the BRS mandate?\n1. double\n2. decimal\n3. integer-only";
    expect(parseQuickReplyOptions(text)).toEqual(["double", "decimal", "integer-only"]);
  });

  it("parses a bulleted list using dashes", () => {
    const text = "Pick a scope:\n- Small\n- Medium\n- Large";
    expect(parseQuickReplyOptions(text)).toEqual(["Small", "Medium", "Large"]);
  });

  it("parses lettered options like (a)/(b)/(c)", () => {
    const text = "Choose one:\n(a) Integers only\n(b) Decimals\n(c) Floating point";
    expect(parseQuickReplyOptions(text)).toEqual(["Integers only", "Decimals", "Floating point"]);
  });

  it("strips a bold-markdown label before the colon, keeping the rest as the option text", () => {
    const text = "Which one?\n1. **Light** — a bright theme\n2. **Dark** — a dim theme";
    expect(parseQuickReplyOptions(text)).toEqual(["Light — a bright theme", "Dark — a dim theme"]);
  });

  it("returns null when there are 5 or more options (too many for buttons)", () => {
    const text = "Pick:\n1. one\n2. two\n3. three\n4. four\n5. five";
    expect(parseQuickReplyOptions(text)).toBeNull();
  });

  it("returns null when there is only one list item (not a real choice)", () => {
    const text = "Notes:\n1. just one item";
    expect(parseQuickReplyOptions(text)).toBeNull();
  });

  it("returns null when there is no list at all", () => {
    expect(parseQuickReplyOptions("Just a plain sentence with no options.")).toBeNull();
  });

  it("returns null for an empty or missing body", () => {
    expect(parseQuickReplyOptions("")).toBeNull();
    expect(parseQuickReplyOptions(undefined)).toBeNull();
  });

  it("ignores an unordered list buried mid-message that isn't the trailing question", () => {
    // A code snippet or explanation earlier in the message containing dash-prefixed lines
    // (e.g. a bullet summary) shouldn't be mistaken for the live question if the message
    // doesn't end on that list.
    const text = "Earlier I did:\n- step one\n- step two\n\nAnyway, how should I proceed next?";
    expect(parseQuickReplyOptions(text)).toBeNull();
  });

  it("caps each option's rendered length so a long list item doesn't become an unreadable button", () => {
    const longOption = "a".repeat(200);
    const text = `Pick:\n1. ${longOption}\n2. short`;
    expect(parseQuickReplyOptions(text)).toBeNull();
  });
});
