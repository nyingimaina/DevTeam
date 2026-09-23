import { stageLabel, stageOutputLabel, phaseLabel, statusLabel, whatsNext, errorKindLabel, repairNotice } from "./labels";

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
    expect(phaseLabel("Gates")).toBe("Checking the work");
    expect(phaseLabel("Challenge")).toBe("Review in progress");
    expect(phaseLabel("Signoff")).toBe("Waiting for your approval");
  });

  it("passes through unknown phases", () => {
    expect(phaseLabel("Nuclear")).toBe("Nuclear");
  });
});

describe("statusLabel", () => {
  it("maps known release statuses to friendly names", () => {
    expect(statusLabel("InProgress")).toBe("In progress");
    expect(statusLabel("BlockedGate")).toBe("Needs attention — some checks didn't pass");
    expect(statusLabel("BlockedSignoff")).toBe("Waiting for your approval");
    expect(statusLabel("Blocked")).toBe("Blocked");
    expect(statusLabel("Ready")).toBe("Ready");
    expect(statusLabel("Complete")).toBe("Done");
  });

  it("maps Escalated to a friendly, retry-implying name", () => {
    expect(statusLabel("Escalated")).toMatch(/error|retry/i);
  });

  it("maps BlockedEntry to a name distinct from BlockedGate — it's the previous stage's problem, not this one's", () => {
    expect(statusLabel("BlockedEntry")).toMatch(/entry|before|start/i);
    expect(statusLabel("BlockedEntry")).not.toBe(statusLabel("BlockedGate"));
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
    expect(errorKindLabel("Stalled")).toMatch(/stopped responding/i);
    expect(errorKindLabel("ProviderUnavailable")).toMatch(/AI service refused/i);
    expect(errorKindLabel("ProviderUnavailable")).toMatch(/different AI model/i);
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

describe("plain-language wording", () => {
  it("never shows internal jargon in status or phase labels", () => {
    const all = [
      ...["Gates", "Signoff", "Challenge"].map(phaseLabel),
      ...["BlockedGate", "BlockedSignoff", "BlockedEntry", "Escalated"].map(statusLabel),
    ].join(" ");
    expect(all).not.toMatch(/gate|signoff|gherkin|entry/i);
  });
});

describe("repairNotice", () => {
  it("is silent when nothing needed fixing", () => {
    expect(repairNotice({ outcome: "Passed", autoFixAttempts: 0, problems: [] })).toBeNull();
  });

  it("tells the user the agent fixed things itself", () => {
    const n = repairNotice({ outcome: "Passed", autoFixAttempts: 1, problems: [] });
    expect(n?.tone).toBe("info");
    expect(n?.title).toMatch(/fixed/i);
  });

  it("lists each remaining problem in plain words when it needs the user", () => {
    const n = repairNotice({
      outcome: "NeedsYou",
      autoFixAttempts: 2,
      problems: [{
        gateName: "gherkin_validator",
        title: "Every requirement has a clear example",
        whatWentWrong: "Requirement 4 doesn't describe the starting situation.",
        technicalDetail: "fail: REQ-004",
      }],
    });
    expect(n?.tone).toBe("error");
    expect(n?.title).toMatch(/2/);
    expect(n?.items).toEqual(["Every requirement has a clear example — Requirement 4 doesn't describe the starting situation."]);
    expect(JSON.stringify(n)).not.toMatch(/gherkin|REQ-004/i);
  });

  it("handles a missing result", () => {
    expect(repairNotice(undefined)).toBeNull();
  });
});