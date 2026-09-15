import {
  ActiveTurnInfo,
  AgentViewModel,
  FileSystemEntryDto,
  FileSystemRootDto,
  FileSystemStatDto,
  GitCredentialsDto,
  GitRemoteDto,
  GitStatusDto,
  HealthResponse,
  MessageDto,
  ModelOption,
  PipelineStageDto,
  ReleaseDto,
  ReleaseFeatureDto,
  SessionDetail,
  StageArtifactDto,
  StagePromptResult,
  StageRunDto,
  StoppedProcessDto,
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

  getSessionAsync(sessionId: string): Promise<SessionDetail> {
    return this.requestAsync<SessionDetail>(`/api/sessions/${sessionId}`);
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

  revealInExplorerAsync(path: string): Promise<void> {
    return this.requestAsync<void>("/api/fs/reveal", {
      method: "POST",
      body: JSON.stringify({ path }),
    });
  }

  cleanupWorkspaceAsync(workspacePath: string): Promise<StoppedProcessDto[]> {
    return this.requestAsync<StoppedProcessDto[]>("/api/fs/cleanup", {
      method: "POST",
      body: JSON.stringify({ workspacePath }),
    });
  }

  getCurrentTurnAsync(): Promise<ActiveTurnInfo | undefined> {
    return this.requestAsync<ActiveTurnInfo | undefined>("/api/turns/current");
  }

  // A 404 here just means the turn already finished before the cancel arrived —
  // an expected race, not an error, so this reports success/failure via the
  // return value instead of throwing like requestAsync would.
  async cancelCurrentTurnAsync(): Promise<boolean> {
    const response = await fetch("/api/turns/current/cancel", { method: "POST" });
    return response.ok;
  }

  // ─── release endpoints ────────────────────────────────────────────────

  createReleaseAsync(featureKey: string, workspacePath: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>("/api/releases", {
      method: "POST",
      body: JSON.stringify({ featureKey, workspacePath }),
    });
  }

  listReleasesAsync(workspacePath?: string): Promise<ReleaseDto[]> {
    const query = workspacePath ? `?workspacePath=${encodeURIComponent(workspacePath)}` : "";
    return this.requestAsync<ReleaseDto[]>(`/api/releases${query}`);
  }

  getReleaseAsync(releaseId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}`);
  }

  advanceFeatureAsync(featureId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/features/${featureId}/advance`, {
      method: "POST",
    });
  }

  signoffFeatureAsync(featureId: string, stageName: string, role: string, comment?: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/features/${featureId}/signoff`, {
      method: "POST",
      body: JSON.stringify({ stageName, role, comment }),
    });
  }

  createFeatureAsync(releaseId: string, featureKey: string): Promise<ReleaseFeatureDto> {
    return this.requestAsync<ReleaseFeatureDto>(`/api/releases/${releaseId}/features`, {
      method: "POST",
      body: JSON.stringify({ featureKey }),
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

  getGitRemoteAsync(workspacePath: string): Promise<GitRemoteDto> {
    return this.requestAsync<GitRemoteDto>(`/api/git/remote?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  setGitRemoteAsync(workspacePath: string, url: string, credentialName?: string | null): Promise<GitRemoteDto> {
    return this.requestAsync<GitRemoteDto>("/api/git/remote", {
      method: "POST",
      body: JSON.stringify({ workspacePath, url, credentialName }),
    });
  }

  setGitCredentialAsync(name: string, token: string): Promise<{ ok: boolean }> {
    return this.requestAsync<{ ok: boolean }>("/api/git/credential", {
      method: "POST",
      body: JSON.stringify({ name, token }),
    });
  }

  listGitCredentialsAsync(): Promise<GitCredentialsDto> {
    return this.requestAsync<GitCredentialsDto>("/api/git/credentials");
  }

  // ─── stage endpoints ────────────────────────────────────────────────────

  startStageAsync(featureId: string): Promise<StageRunDto> {
    return this.requestAsync<StageRunDto>(`/api/features/${featureId}/start-stage`, {
      method: "POST",
    });
  }

  sendStageMessageAsync(featureId: string, text: string): Promise<StagePromptResult> {
    return this.requestAsync<StagePromptResult>(`/api/features/${featureId}/send-message`, {
      method: "POST",
      body: JSON.stringify({ text }),
    });
  }

  runStageGatesAsync(featureId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/features/${featureId}/run-gates`, {
      method: "POST",
    });
  }

  getPipelineAsync(featureId: string): Promise<PipelineStageDto[]> {
    return this.requestAsync<PipelineStageDto[]>(`/api/features/${featureId}/pipeline`);
  }

  getAvailableModelsAsync(releaseId: string): Promise<ModelOption[]> {
    return this.requestAsync<ModelOption[]>(`/api/releases/${releaseId}/models`);
  }

  getStageMessagesAsync(featureId: string, stageRunId: string): Promise<MessageDto[]> {
    return this.requestAsync<MessageDto[]>(`/api/features/${featureId}/stages/${stageRunId}/messages`);
  }

  getStageArtifactsAsync(featureId: string, stageRunId: string): Promise<StageArtifactDto[]> {
    return this.requestAsync<StageArtifactDto[]>(`/api/features/${featureId}/stages/${stageRunId}/artifacts`);
  }

  getWorkspaceChangesAsync(featureId: string): Promise<string[]> {
    return this.requestAsync<string[]>(`/api/features/${featureId}/workspace-changes`);
  }

  runStageAsync(featureId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/features/${featureId}/run-stage`, {
      method: "POST",
    });
  }

  pushBackAsync(featureId: string, targetStageName: string, instructions: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/features/${featureId}/push-back`, {
      method: "POST",
      body: JSON.stringify({ targetStageName, instructions }),
    });
  }
}