import { NegotiationPointDto } from "../../Chat/Data/BrokerTypes";
import { negotiationHeading, pointResponseLabel, pointStatusLabel } from "./negotiation";

function point(partial: Partial<NegotiationPointDto>): NegotiationPointDto {
  return {
    id: "p1",
    target: "coverage_matrix",
    summary: "A test must name the requirement it proves",
    round: 1,
    status: "Open",
    responseKind: "None",
    createdAt: "2026-09-22T00:00:00Z",
    ...partial,
  };
}

describe("negotiationHeading", () => {
  it("counts the open points and names the round", () => {
    const heading = negotiationHeading([
      point({ id: "a", round: 2, status: "Open" }),
      point({ id: "b", round: 2, status: "Open" }),
      point({ id: "c", round: 1, status: "Resolved" }),
    ]);

    expect(heading).toContain("round 2");
    expect(heading).toContain("2 points still open");
  });

  it("says everything is addressed when nothing is open", () => {
    expect(negotiationHeading([point({ status: "Resolved" })])).toContain("everything addressed");
  });

  it("is empty with no points", () => {
    expect(negotiationHeading([])).toBe("");
  });
});

describe("point labels", () => {
  it("describes every response kind in plain words", () => {
    expect(pointResponseLabel("Addressed")).toMatch(/done/i);
    expect(pointResponseLabel("Disputed")).toMatch(/disagree/i);
    expect(pointResponseLabel("Blocked")).toMatch(/can't|cannot/i);
    expect(pointResponseLabel("None")).toMatch(/no answer/i);
  });

  it("describes every status in plain words", () => {
    expect(pointStatusLabel("Resolved")).toMatch(/closed/i);
    expect(pointStatusLabel("Escalated")).toMatch(/decision/i);
    expect(pointStatusLabel("Open")).toBe("Open");
  });

  it("never shows internal jargon", () => {
    const all = [
      negotiationHeading([point({ round: 3 })]),
      ...(["Open", "Resolved", "Escalated"] as const).map(pointStatusLabel),
      ...(["None", "Addressed", "Disputed", "Blocked"] as const).map(pointResponseLabel),
    ].join(" ");

    expect(all).not.toMatch(/gate|push.?back|round.?trip|gherkin|coverage_matrix|responsibleRole/i);
  });
});
