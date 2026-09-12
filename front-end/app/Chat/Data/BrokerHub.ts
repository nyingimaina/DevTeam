import { HubConnection, HubConnectionBuilder } from "@microsoft/signalr";
import { StreamEvent } from "./BrokerTypes";

export type StreamEventHandler = (evt: StreamEvent) => void;

export default class BrokerHub {
  private connection: HubConnection | null = null;
  private handlers = new Set<StreamEventHandler>();

  async startAsync(): Promise<void> {
    if (this.connection?.state === "Connected" || this.connection?.state === "Connecting") return;
    this.connection = new HubConnectionBuilder()
      .withUrl("/hub")
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => Math.min(5000, Math.random() * 500) })
      .build();

    this.connection.on("OnEvent", (evt: StreamEvent) => {
      for (const handler of this.handlers) handler(evt);
    });

    await this.connection.start();
  }

  async joinSessionAsync(sessionId: string): Promise<void> {
    if (!this.connection) return;
    await this.connection.invoke("JoinSession", sessionId);
  }

  async leaveSessionAsync(sessionId: string): Promise<void> {
    if (!this.connection) return;
    await this.connection.invoke("LeaveSession", sessionId);
  }

  addHandler(handler: StreamEventHandler): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  async disposeAsync(): Promise<void> {
    if (this.connection) {
      await this.connection.stop();
      this.connection = null;
    }
  }
}