import ModuleStateManager from "module-state-manager";
import ChatRepository from "./ChatRepository";
import BrokerApi from "../Data/BrokerApi";
import { StreamEvent, StreamEventType, TextDeltaPayload, ToolCallPayload, MessageDto } from "../Data/BrokerTypes";
import BrokerHub from "../Data/BrokerHub";

export default class ChatLogic extends ModuleStateManager<ChatRepository> {
  repository: ChatRepository = new ChatRepository();
  model: {} = {};

  private api: BrokerApi;
  private hub: BrokerHub;
  private hubCleanup?: () => void;

  constructor(api: BrokerApi = new BrokerApi(), hub: BrokerHub = new BrokerHub()) {
    super();
    this.api = api;
    this.hub = hub;
  }

  public async initializeAsync(): Promise<void> {
    this.updateRepository({ busy: true, error: undefined });
    try {
      const [health, info, sessions] = await Promise.all([
        this.api.getHealthAsync(),
        this.api.getInfoAsync(),
        this.api.listSessionsAsync(),
      ]);
      const latestSession = sessions.length > 0 ? sessions[0] : undefined;
      const activeSession = latestSession
        ? await this.api.getSessionAsync(latestSession.sessionId)
        : undefined;

      this.updateRepository({
        brokerReady: true,
        brokerVersion: health.version ?? undefined,
        agentName: info.agentName,
        sessions,
        activeSession,
        currentModelId: activeSession?.modelId ?? undefined,
        currentModeId: activeSession?.modeId ?? undefined,
      });

      await this.hub.startAsync();
      this.hubCleanup = this.hub.addHandler((evt) => this.handleStreamEvent(evt));
      if (activeSession) await this.hub.joinSessionAsync(activeSession.sessionId);
    } catch (err) {
      this.updateRepository({
        brokerReady: false,
        error: `Failed to connect to the broker. ${err instanceof Error ? err.message : String(err)}`,
      });
    } finally {
      this.updateRepository({ busy: false });
    }
  }

  public async createSessionAsync(workspacePath: string): Promise<void> {
    this.updateRepository({ busy: true, error: undefined });
    try {
      const newSession = await this.api.createSessionAsync(workspacePath, this.repository.currentModelId);
      const detail = await this.api.getSessionAsync(newSession.sessionId);
      this.updateRepository({
        activeSession: detail,
        currentModelId: detail.modelId ?? this.repository.currentModelId,
        currentModeId: detail.modeId ?? this.repository.currentModeId,
        sessions: [newSession, ...this.repository.sessions],
      });
      await this.hub.joinSessionAsync(detail.sessionId);
    } catch (err) {
      this.updateRepository({ error: `Failed to create session: ${err instanceof Error ? err.message : String(err)}` });
    } finally {
      this.updateRepository({ busy: false });
    }
  }

  public async switchSessionAsync(sessionId: string): Promise<void> {
    const previous = this.repository.activeSession;
    if (previous?.sessionId === sessionId) return;
    this.updateRepository({ busy: true, error: undefined, liveAssistant: undefined });
    try {
      if (previous) await this.hub.leaveSessionAsync(previous.sessionId);
      const detail = await this.api.getSessionAsync(sessionId);
      this.updateRepository({
        activeSession: detail,
        currentModelId: detail.modelId ?? this.repository.currentModelId,
        currentModeId: detail.modeId ?? this.repository.currentModeId,
      });
      await this.hub.joinSessionAsync(sessionId);
    } catch (err) {
      this.updateRepository({ error: `Failed to switch session: ${err instanceof Error ? err.message : String(err)}` });
    } finally {
      this.updateRepository({ busy: false });
    }
  }

  public async sendPromptAsync(text: string): Promise<void> {
    const session = this.repository.activeSession;
    if (!session || !text.trim()) return;

    const optimisticUser: MessageDto = {
      id: `temp-${Date.now()}`,
      role: "user",
      bodyText: text,
      createdAt: new Date().toISOString(),
      parts: [],
    };

    this.updateRepository({
      isPending: true,
      error: undefined,
      liveAssistant: { text: "", toolCalls: [] },
      activeSession: {
        ...session,
        messages: [...session.messages, optimisticUser],
      },
    });

    try {
      await this.api.promptAsync(session.sessionId, text);
    } catch (err) {
      this.updateRepository({
        isPending: false,
        liveAssistant: undefined,
        error: `Prompt failed: ${err instanceof Error ? err.message : String(err)}`,
        activeSession: {
          ...this.repository.activeSession!,
          messages: this.repository.activeSession!.messages.filter((m) => m.id !== optimisticUser.id),
        },
      });
    }
  }

  public async switchModelAsync(modelId: string): Promise<void> {
    const session = this.repository.activeSession;
    if (!session) return;
    try {
      await this.api.setModelAsync(session.sessionId, modelId);
      this.updateRepository({ currentModelId: modelId, activeSession: { ...session, modelId } });
    } catch (err) {
      this.updateRepository({ error: `Failed to switch model: ${err instanceof Error ? err.message : String(err)}` });
    }
  }

  public async switchModeAsync(modeId: string): Promise<void> {
    const session = this.repository.activeSession;
    if (!session) return;
    try {
      await this.api.setModeAsync(session.sessionId, modeId);
      this.updateRepository({ currentModeId: modeId, activeSession: { ...session, modeId } });
    } catch (err) {
      this.updateRepository({ error: `Failed to switch mode: ${err instanceof Error ? err.message : String(err)}` });
    }
  }

  public async deleteSessionAsync(sessionId: string): Promise<void> {
    try {
      await this.hub.leaveSessionAsync(sessionId);
      await this.api.deleteSessionAsync(sessionId);
      const remaining = this.repository.sessions.filter((s) => s.sessionId !== sessionId);
      const activeSession =
        this.repository.activeSession?.sessionId === sessionId ? undefined : this.repository.activeSession;
      if (!activeSession && remaining.length > 0) {
        const detail = await this.api.getSessionAsync(remaining[0].sessionId);
        await this.hub.joinSessionAsync(detail.sessionId);
        this.updateRepository({
          sessions: remaining,
          activeSession: detail,
          currentModelId: detail.modelId ?? undefined,
          currentModeId: detail.modeId ?? undefined,
        });
      } else {
        this.updateRepository({
          sessions: remaining,
          activeSession,
          currentModelId: activeSession?.modelId ?? undefined,
          currentModeId: activeSession?.modeId ?? undefined,
        });
      }
    } catch (err) {
      this.updateRepository({ error: `Failed to delete session: ${err instanceof Error ? err.message : String(err)}` });
    }
  }

  public async disposeAsync(): Promise<void> {
    this.hubCleanup?.();
    await this.hub.disposeAsync();
  }

  private handleStreamEvent(evt: StreamEvent): void {
    if (evt.sessionId !== this.repository.activeSession?.sessionId) return;
    const { activeSession } = this.repository;
    if (!activeSession) return;

    switch (evt.type) {
      case StreamEventType.TextDelta: {
        const p = evt.payload as TextDeltaPayload;
        const live = this.repository.liveAssistant;
        this.updateRepository({
          liveAssistant: {
            ...(live ?? { text: "", toolCalls: [] }),
            messageId: p.messageId,
            text: live?.text + p.text,
          },
        });
        break;
      }
      case StreamEventType.ThoughtDelta:
        break;
      case StreamEventType.ToolCall: {
        const p = evt.payload as ToolCallPayload;
        const live = this.repository.liveAssistant ?? { text: "", toolCalls: [] };
        this.updateRepository({
          liveAssistant: {
            ...live,
            toolCalls: [...live.toolCalls, { toolCallId: p.toolCallId, title: p.title, kind: p.kind }],
          },
        });
        break;
      }
      case StreamEventType.TurnEnd: {
        this.updateRepository({ liveAssistant: undefined, isPending: false });
        this.reloadSessionAsync(activeSession.sessionId);
        break;
      }
      case StreamEventType.Error:
        this.updateRepository({
          liveAssistant: undefined,
          isPending: false,
          error: JSON.stringify(evt.payload),
        });
        break;
      default:
        break;
    }
  }

  private async reloadSessionAsync(sessionId: string): Promise<void> {
    try {
      const detail = await this.api.getSessionAsync(sessionId);
      this.updateRepository({ activeSession: detail });
    } catch {
      this.updateRepository({ error: "Failed to reload session after turn." });
    }
  }
}