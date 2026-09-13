import {
  AgentViewModel,
  FileSystemEntryDto,
  FileSystemRootDto,
  FileSystemStatDto,
  GitStatusDto,
  HealthResponse,
  PromptResponse,
  ReleaseDto,
  SessionDetail,
  SessionSummary,
  StagePromptResult,
  StageRunDto,
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

  // ─── release endpoints ────────────────────────────────────────────────

  createReleaseAsync(featureKey: string, workspacePath: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>("/api/releases", {
      method: "POST",
      body: JSON.stringify({ featureKey, workspacePath }),
    });
  }

  listReleasesAsync(): Promise<ReleaseDto[]> {
    return this.requestAsync<ReleaseDto[]>("/api/releases");
  }

  getReleaseAsync(releaseId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}`);
  }

  advanceReleaseAsync(releaseId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}/advance`, {
      method: "POST",
    });
  }

  signoffReleaseAsync(releaseId: string, stageName: string, role: string, comment?: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}/signoff`, {
      method: "POST",
      body: JSON.stringify({ stageName, role, comment }),
    });
  }

  // ─── git endpoints ────────────────────────────────────────────────────

  initGitAsync(workspacePath: string): Promise<GitStatusDto> {
    return this.requestAsync<GitStatusDto>("/api/git/init", {
      method: "POST",
      body: JSON.stringify({ workspacePath }),
    });
  }

  getGitStatusAsync(workspacePath: string): Promise<GitStatusDto> {
    return this.requestAsync<GitStatusDto>(`/api/git/status?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  createGitBranchAsync(workspacePath: string, branchName: string): Promise<GitStatusDto> {
    return this.requestAsync<GitStatusDto>("/api/git/branch", {
      method: "POST",
      body: JSON.stringify({ workspacePath, branchName }),
    });
  }

  commitGitAsync(workspacePath: string, message: string): Promise<GitStatusDto> {
    return this.requestAsync<GitStatusDto>("/api/git/commit", {
      method: "POST",
      body: JSON.stringify({ workspacePath, message }),
    });
  }

  mergeGitAsync(workspacePath: string, sourceBranch: string, targetBranch?: string): Promise<GitStatusDto> {
    return this.requestAsync<GitStatusDto>("/api/git/merge", {
      method: "POST",
      body: JSON.stringify({ workspacePath, sourceBranch, targetBranch }),
    });
  }

  getGitLogAsync(workspacePath: string): Promise<GitStatusDto> {
    return this.requestAsync<GitStatusDto>(`/api/git/log?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  // ─── stage endpoints ────────────────────────────────────────────────────

  startStageAsync(releaseId: string): Promise<StageRunDto> {
    return this.requestAsync<StageRunDto>(`/api/releases/${releaseId}/start-stage`, {
      method: "POST",
    });
  }

  sendStageMessageAsync(releaseId: string, text: string): Promise<StagePromptResult> {
    return this.requestAsync<StagePromptResult>(`/api/releases/${releaseId}/send-message`, {
      method: "POST",
      body: JSON.stringify({ text }),
    });
  }

  runStageGatesAsync(releaseId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}/run-gates`, {
      method: "POST",
    });
  }
}