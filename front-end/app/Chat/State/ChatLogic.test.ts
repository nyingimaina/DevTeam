import ChatLogic from "./ChatLogic";
import BrokerApi from "../Data/BrokerApi";
import BrokerHub from "../Data/BrokerHub";
import { SessionDetail, SessionSummary, StreamEvent, StreamEventType } from "../Data/BrokerTypes";

class FakeHub extends BrokerHub {
  joined: string[] = [];
  handlers: ((evt: StreamEvent) => void)[] = [];

  async startAsync() {}
  async joinSessionAsync(sessionId: string) {
    this.joined.push(sessionId);
  }
  async leaveSessionAsync(sessionId: string) {
    this.joined = this.joined.filter((id) => id !== sessionId);
  }
  async disposeAsync() {}
  addHandler(handler: (evt: StreamEvent) => void): () => void {
    this.handlers.push(handler);
    return () => {};
  }
}

function makeLogic(api: BrokerApi, hub: BrokerHub): ChatLogic {
  const logic = new ChatLogic(api, hub);
  logic.setRerender(() => {});
  return logic;
}

function makeSessionDetails(sessionId: string, modelId: string): SessionDetail {
  return {
    sessionId,
    acpSessionId: `acp-${sessionId}`,
    workspacePath: `C:\\work\\${sessionId}`,
    title: null,
    modelId,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    models: [{ value: modelId, name: modelId }],
    messages: [],
  };
}

function makeSessionSummaries(details: SessionDetail[]): SessionSummary[] {
  return details.map((d) => ({
    sessionId: d.sessionId,
    acpSessionId: d.acpSessionId,
    workspacePath: d.workspacePath,
    title: d.title,
    modelId: d.modelId,
    createdAt: d.createdAt,
    updatedAt: d.updatedAt,
    models: d.models,
  }));
}

describe("ChatLogic", () => {
  it("initializes with the latest session as active", async () => {
    const detail = makeSessionDetails("s1", "opencode/big-pickle");
    const summary = makeSessionSummaries([detail]);
    const api = {
      getHealthAsync: jest.fn().mockResolvedValue({ status: "ok", version: "0.1.0" }),
      getInfoAsync: jest.fn().mockResolvedValue({ protocolVersion: "1", agentName: "OpenCode", agentVendor: "anomalyco", agentVersion: "1", models: [] }),
      listSessionsAsync: jest.fn().mockResolvedValue(summary),
      getSessionAsync: jest.fn().mockResolvedValue(detail),
      createSessionAsync: jest.fn(),
      promptAsync: jest.fn(),
      setModelAsync: jest.fn(),
      deleteSessionAsync: jest.fn(),
    } as unknown as BrokerApi;
    const hub = new FakeHub();

    const logic = makeLogic(api, hub);
    await logic.initializeAsync();

    expect(logic.repository.brokerReady).toBe(true);
    expect(logic.repository.activeSession?.sessionId).toBe("s1");
    expect(logic.repository.currentModelId).toBe("opencode/big-pickle");
    expect(hub.joined).toContain("s1");
  });

  it("appends streamed text deltas to the live assistant bubble", async () => {
    const detail = makeSessionDetails("s1", "opencode/big-pickle");
    const api = {
      getHealthAsync: jest.fn().mockResolvedValue({ status: "ok", version: "0.1.0" }),
      getInfoAsync: jest.fn().mockResolvedValue({ protocolVersion: "1", agentName: "a", agentVendor: "b", agentVersion: "1", models: [] }),
      listSessionsAsync: jest.fn().mockResolvedValue(makeSessionSummaries([detail])),
      getSessionAsync: jest.fn().mockResolvedValue(detail),
      promptAsync: jest.fn().mockResolvedValue({ sessionId: "s1", stopReason: "end_turn", inputTokens: 1, outputTokens: 1, totalTokens: 2 }),
      setModelAsync: jest.fn(),
      deleteSessionAsync: jest.fn(),
    } as unknown as BrokerApi;
    const hub = new FakeHub();

    const logic = makeLogic(api, hub);
    await logic.initializeAsync();
    void logic.sendPromptAsync("hi");
    await (api.promptAsync as jest.Mock).mock.results[0];

    const emit = (text: string) => {
      const evt: StreamEvent = { sessionId: "s1", type: StreamEventType.TextDelta, payload: { messageId: "m1", text } };
      for (const h of hub.handlers) h(evt);
    };
    emit("He");
    emit("llo");

    expect(logic.repository.liveAssistant?.text).toBe("Hello");
    expect(logic.repository.activeSession?.messages.length).toBe(1);
  });

  it("clears the live bubble on turnEnd", async () => {
    const detail = makeSessionDetails("s1", "opencode/big-pickle");
    const api = {
      getHealthAsync: jest.fn().mockResolvedValue({ status: "ok", version: "0.1.0" }),
      getInfoAsync: jest.fn().mockResolvedValue({ protocolVersion: "1", agentName: "a", agentVendor: "b", agentVersion: "1", models: [] }),
      listSessionsAsync: jest.fn().mockResolvedValue(makeSessionSummaries([detail])),
      getSessionAsync: jest.fn().mockResolvedValue(detail),
      promptAsync: jest.fn().mockResolvedValue({ sessionId: "s1", stopReason: "end_turn", inputTokens: 2, outputTokens: 2, totalTokens: 4 }),
      setModelAsync: jest.fn(),
      deleteSessionAsync: jest.fn(),
    } as unknown as BrokerApi;
    const hub = new FakeHub();

    const logic = makeLogic(api, hub);
    await logic.initializeAsync();
    void logic.sendPromptAsync("hi");
    await (api.promptAsync as jest.Mock).mock.results[0];

    const emit = (type: string, payload: unknown) => {
      const evt: StreamEvent = { sessionId: "s1", type, payload };
      for (const h of hub.handlers) h(evt);
    };
    emit(StreamEventType.TextDelta, { messageId: "m1", text: "done" });
    expect(logic.repository.liveAssistant?.text).toBe("done");

    emit(StreamEventType.TurnEnd, { stopReason: "end_turn", usage: null });
    expect(logic.repository.liveAssistant).toBeUndefined();
    expect(logic.repository.isPending).toBe(false);
  });

  it("switches active session on switchSessionAsync", async () => {
    const d1 = makeSessionDetails("s1", "model-a");
    const d2 = makeSessionDetails("s2", "model-b");
    const api = {
      getHealthAsync: jest.fn().mockResolvedValue({ status: "ok", version: "0.1.0" }),
      getInfoAsync: jest.fn().mockResolvedValue({ protocolVersion: "1", agentName: "a", agentVendor: "b", agentVersion: "1", models: [] }),
      listSessionsAsync: jest.fn().mockResolvedValue(makeSessionSummaries([d1, d2])),
      getSessionAsync: jest.fn().mockImplementation(async (id: string) => (id === "s1" ? d1 : d2)),
      createSessionAsync: jest.fn(),
      promptAsync: jest.fn(),
      setModelAsync: jest.fn(),
      deleteSessionAsync: jest.fn(),
    } as unknown as BrokerApi;
    const hub = new FakeHub();

    const logic = makeLogic(api, hub);
    await logic.initializeAsync();
    await logic.switchSessionAsync("s2");

    expect(logic.repository.activeSession?.sessionId).toBe("s2");
    expect(logic.repository.currentModelId).toBe("model-b");
    expect(hub.joined).toEqual(["s2"]);
  });
});