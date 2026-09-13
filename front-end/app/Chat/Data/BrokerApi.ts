import {
  AgentViewModel,
  FileSystemEntryDto,
  FileSystemRootDto,
  FileSystemStatDto,
  HealthResponse,
  PromptResponse,
  SessionDetail,
  SessionSummary,
} from "./BrokerTypes";

export default class BrokerApi {
  private async requestAsync<T>(url: string, options: RequestInit = {}): Promise<T> {
    const response = await fetch(url, {
      headers: { "Content-Type": "application/json" },
      ...options,
    });
    if (!response.ok) {
      const body = await response.text().catch(() => "");
      throw new Error(`Broker ${options.method ?? "GET"} ${url} failed with ${response.status}: ${body}`);
    }
    if (response.status === 204) return undefined as T;
    return (await response.json()) as T;
  }

  getHealthAsync(): Promise<HealthResponse> {
    return this.requestAsync<HealthResponse>("/healthz");
  }

  getInfoAsync(): Promise<AgentViewModel> {
    return this.requestAsync<AgentViewModel>("/api/info");
  }

  listSessionsAsync(): Promise<SessionSummary[]> {
    return this.requestAsync<SessionSummary[]>("/api/sessions");
  }

  createSessionAsync(workspacePath: string, modelId?: string | null): Promise<SessionSummary> {
    return this.requestAsync<SessionSummary>("/api/sessions", {
      method: "POST",
      body: JSON.stringify({ workspacePath, modelId }),
    });
  }

  getSessionAsync(sessionId: string): Promise<SessionDetail> {
    return this.requestAsync<SessionDetail>(`/api/sessions/${sessionId}`);
  }

  deleteSessionAsync(sessionId: string): Promise<void> {
    return this.requestAsync<void>(`/api/sessions/${sessionId}`, { method: "DELETE" });
  }

  promptAsync(sessionId: string, text: string): Promise<PromptResponse> {
    return this.requestAsync<PromptResponse>(`/api/sessions/${sessionId}/prompt`, {
      method: "POST",
      body: JSON.stringify({ text }),
    });
  }

  setModelAsync(sessionId: string, modelId: string): Promise<{ modelId: string }> {
    return this.requestAsync<{ modelId: string }>(`/api/sessions/${sessionId}/model`, {
      method: "POST",
      body: JSON.stringify({ modelId }),
    });
  }

  setModeAsync(sessionId: string, modeId: string): Promise<{ modeId: string }> {
    return this.requestAsync<{ modeId: string }>(`/api/sessions/${sessionId}/mode`, {
      method: "POST",
      body: JSON.stringify({ modeId }),
    });
  }

  listFileSystemRootsAsync(): Promise<FileSystemRootDto[]> {
    return this.requestAsync<FileSystemRootDto[]>("/api/fs/roots");
  }

  listDirectoryAsync(path: string): Promise<FileSystemEntryDto[]> {
    return this.requestAsync<FileSystemEntryDto[]>(`/api/fs/list?path=${encodeURIComponent(path)}`);
  }

  getFileSystemStatAsync(path: string): Promise<FileSystemStatDto> {
    return this.requestAsync<FileSystemStatDto>(`/api/fs/stat?path=${encodeURIComponent(path)}`);
  }

  createDirectoryAsync(path: string): Promise<FileSystemStatDto> {
    return this.requestAsync<FileSystemStatDto>("/api/fs/mkdir", {
      method: "POST",
      body: JSON.stringify({ path }),
    });
  }
}