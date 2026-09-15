import { stageLabel, stageOutputLabel, phaseLabel, statusLabel, whatsNext, errorKindLabel } from "./labels";

describe("stageLabel", () => {
  it("maps known pipeline stages to friendly names", () => {
    expect(stageLabel("business-analyst")).toBe("Business Analyst");
    expect(stageLabel("developer")).toBe("Developer");
    expect(stageLabel("qa")).toBe("QA");
  });

  it("falls back to a friendly title-case of unknown stage names", () => {
    expect(stageLabel("tech-writer")).toBe("Tech Writer");
    expect(stageLabel("qa")).toBe("QA");
  });
});

describe("stageOutputLabel", () => {
  it("calls the business analyst's output BRS, not a generic 'output'", () => {
    expect(stageOutputLabel("business-analyst")).toBe("BRS");
  });

  it("falls back to '<stage> output' for stages without a named document", () => {
    expect(stageOutputLabel("developer")).toBe("Developer output");
    expect(stageOutputLabel("qa")).toBe("QA output");
  });
});

describe("phaseLabel", () => {
  it("maps known phases to friendly names", () => {
    expect(phaseLabel("GuidedQA")).toBe("Chat with agent");
    expect(phaseLabel("Producing")).toBe("Producing artifacts");
    expect(phaseLabel("Gates")).toBe("Running gates");
    expect(phaseLabel("Challenge")).toBe("Review in progress");
    expect(phaseLabel("Signoff")).toBe("Awaiting signoff");
  });

  it("passes through unknown phases", () => {
    expect(phaseLabel("Nuclear")).toBe("Nuclear");
  });
});

describe("statusLabel", () => {
  it("maps known release statuses to friendly names", () => {
    expect(statusLabel("InProgress")).toBe("In progress");
    expect(statusLabel("BlockedGate")).toBe("Blocked — gates failing");
    expect(statusLabel("BlockedSignoff")).toBe("Awaiting your signoff");
    expect(statusLabel("Blocked")).toBe("Blocked");
    expect(statusLabel("Ready")).toBe("Ready");
    expect(statusLabel("Complete")).toBe("Done");
  });

  it("maps Escalated to a friendly, retry-implying name", () => {
    expect(statusLabel("Escalated")).toMatch(/error|retry/i);
  });

  it("passes through unknown statuses", () => {
    expect(statusLabel("Weird")).toBe("Weird");
  });
});

describe("errorKindLabel", () => {
  it("gives a specific, distinct reason for each error kind", () => {
    expect(errorKindLabel("Disconnected")).toMatch(/disconnect|process/i);
    expect(errorKindLabel("ProviderRejected")).toMatch(/provider|rejected/i);
    expect(errorKindLabel("TimedOut")).toMatch(/time/i);
  });

  it("returns empty string for None or unknown", () => {
    expect(errorKindLabel("None")).toBe("");
    expect(errorKindLabel("Mystery")).toBe("");
  });
});

describe("whatsNext", () => {
  it("returns an explicit hint for the interactive analyst stage", () => {
    expect(whatsNext("business-analyst")).toMatch(/answer/i);
    expect(whatsNext("business-analyst")).toContain("DONE");
  });

  it("returns an explicit hint for autonomous stages", () => {
    expect(whatsNext("developer")).toMatch(/watch/i);
    expect(whatsNext("qa")).toMatch(/automatically|watch/i);
  });

  it("returns empty string for unknown stages", () => {
    expect(whatsNext("mystery")).toBe("");
  });
});