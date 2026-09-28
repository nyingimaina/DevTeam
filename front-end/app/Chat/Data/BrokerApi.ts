import {
  ActiveTurnInfo,
  AgentViewModel,
  CheckDefinitionDto,
  CodeContextStatusDto,
  DiagnosticsSettingsDto,
  FileSystemEntryDto,
  FileSystemRootDto,
  FileSystemStatDto,
  GitCredentialsDto,
  GitRemoteDto,
  GitStatusDto,
  HealthResponse,
  MessageDto,
  ModelCandidateDto,
  ModelSwitchResultDto,
  NotificationSettingsDto,
  MetricsSummaryDto,
  ModelOption,
  NegotiationPointDto,
  RequirementProgressDto,
  PipelineEditorDto,
  PipelineEditorRoleDto,
  PipelineStageDto,
  ProfileDto,
  ReadinessReportDto,
  SpecialistRoleDto,
  ReleaseDto,
  RunGatesRepairResultDto,
  ReleaseFeatureDto,
  SemaNamiSettingsDto,
  SessionDetail,
  StageArtifactDto,
  StagePromptResult,
  StageRunDto,
  StoppedProcessDto,
  CleanupResult,
  WorkspaceProfileDto,
  } from "./BrokerTypes";
  import type { ContextCompactionResult, ContextDto } from "./ContextTypes";
  import { recentErrors, recordError } from "../../UI/diagnostics";

export default class BrokerApi {
  private async requestAsync<T>(url: string, options: RequestInit = {}): Promise<T> {
    const response = await fetch(url, {
      headers: { "Content-Type": "application/json" },
      ...options,
    });
    if (!response.ok) {
      const body = (await response.text().catch(() => "")).trim();
      // Record it for the support bundle: the UI's failures travel with the server's logs, and
      // the request reference is what ties this click to a specific line in those logs.
      recordError({
        source: "api",
        message: body || `HTTP ${response.status}`,
        url: `${options.method ?? "GET"} ${url}`,
        status: response.status,
        requestId: response.headers.get("X-Request-Id") ?? undefined,
      });
      // The broker writes plain-language error bodies meant for the person using the app
      // (e.g. "The run was cancelled."), so surface those directly instead of wrapping them in
      // a technical "Broker POST /api/... failed with 409" line. Fall back to the technical
      // form only when there's nothing readable (e.g. a JSON problem blob).
      if (body && !body.startsWith("{") && !body.startsWith("[")) {
        throw new Error(body);
      }
      throw new Error(
        `Broker ${options.method ?? "GET"} ${url} failed with ${response.status}${body ? `: ${body}` : ""}`,
      );
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

  cleanupWorkspaceAsync(workspacePath: string): Promise<CleanupResult> {
    return this.requestAsync<CleanupResult>("/api/fs/cleanup", {
      method: "POST",
      body: JSON.stringify({ workspacePath }),
    });
  }

  // Stops only pids the sweep itself surfaced as approval-needing. The backend refuses anything
  // else, so this can never become a generic kill endpoint.
  approveProcessStopAsync(processIds: number[]): Promise<StoppedProcessDto[]> {
    return this.requestAsync<StoppedProcessDto[]>("/api/fs/cleanup/approve", {
      method: "POST",
      body: JSON.stringify({ processIds }),
    });
  }

  getCurrentTurnAsync(): Promise<ActiveTurnInfo | undefined> {
    return this.requestAsync<ActiveTurnInfo | undefined>("/api/turns/current");
  }

  // ─── context ───────────────────────────────────────────────────────────
  //
  // Polled, not pushed. The agent reports its context once at the end of a turn, so there is no
  // stream of updates to subscribe to — a push transport would deliver the same single reading with
  // extra machinery, and a smoothly filling bar would be showing motion the numbers don't support.

  getContextAsync(): Promise<ContextDto> {
    return this.requestAsync<ContextDto>("/api/context/current");
  }

  // Compacts whatever session the context belongs to. The bar is global and often has no session of
  // its own to name — the broker knows whose context that is.
  compactContextAsync(): Promise<ContextCompactionResult> {
    return this.requestAsync<ContextCompactionResult>("/api/context/compact", { method: "POST" });
  }

  // A 404 here just means the turn already finished before the cancel arrived —
  // an expected race, not an error, so this reports success/failure via the
  // return value instead of throwing like requestAsync would.
  async cancelCurrentTurnAsync(): Promise<boolean> {
    const response = await fetch("/api/turns/current/cancel", { method: "POST" });
    return response.ok;
  }

  // ─── diagnostics (support hand-off) ────────────────────────────────────

  getDiagnosticsSettingsAsync(): Promise<DiagnosticsSettingsDto> {
    return this.requestAsync<DiagnosticsSettingsDto>("/api/diagnostics/settings");
  }

  setVerboseLoggingAsync(verboseLogging: boolean): Promise<DiagnosticsSettingsDto> {
    return this.requestAsync<DiagnosticsSettingsDto>("/api/diagnostics/settings", {
      method: "POST",
      body: JSON.stringify({ verboseLogging }),
    });
  }

  // Sends the UI's own recent errors along with the request, so the returned file has both halves
  // of the incident: what the screen showed, and what the broker logged.
  async collectDiagnosticsAsync(): Promise<Blob> {
    const response = await fetch("/api/diagnostics/bundle", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ frontendErrors: recentErrors() }),
    });
    if (!response.ok) {
      throw new Error(`Could not collect the diagnostics file (HTTP ${response.status}).`);
    }
    return await response.blob();
  }

  revealLogsAsync(): Promise<void> {
    return this.requestAsync<void>("/api/diagnostics/reveal-logs", { method: "POST" });
  }

  // ─── model candidates (the failover list) ──────────────────────────────

  listModelCandidatesAsync(workspacePath: string): Promise<ModelCandidateDto[]> {
    return this.requestAsync<ModelCandidateDto[]>(
      `/api/models/candidates?workspacePath=${encodeURIComponent(workspacePath)}`,
    );
  }

  addModelCandidateAsync(workspacePath: string, modelId: string): Promise<ModelCandidateDto> {
    return this.requestAsync<ModelCandidateDto>("/api/models/candidates", {
      method: "POST",
      body: JSON.stringify({ workspacePath, modelId }),
    });
  }

  removeModelCandidateAsync(id: string): Promise<void> {
    return this.requestAsync<void>(`/api/models/candidates/${id}`, { method: "DELETE" });
  }

  reorderModelCandidatesAsync(workspacePath: string, orderedIds: string[]): Promise<ModelCandidateDto[]> {
    return this.requestAsync<ModelCandidateDto[]>("/api/models/candidates/reorder", {
      method: "POST",
      body: JSON.stringify({ workspacePath, orderedIds }),
    });
  }

  setModelCandidateEnabledAsync(id: string, enabled: boolean): Promise<void> {
    return this.requestAsync<void>(`/api/models/candidates/${id}/enabled`, {
      method: "POST",
      body: JSON.stringify({ enabled }),
    });
  }

  // Everything the agent says it can run, for the "add a model" picker.
  listAvailableModelsAsync(workspacePath: string): Promise<ModelOption[]> {
    return this.requestAsync<ModelOption[]>(
      `/api/models/available?workspacePath=${encodeURIComponent(workspacePath)}`,
    );
  }

  // ─── desktop notifications ─────────────────────────────────────────────

  getNotificationSettingsAsync(): Promise<NotificationSettingsDto> {
    return this.requestAsync<NotificationSettingsDto>("/api/notifications/settings");
  }

  setNotificationSettingsAsync(settings: Partial<NotificationSettingsDto>): Promise<NotificationSettingsDto> {
    return this.requestAsync<NotificationSettingsDto>("/api/notifications/settings", {
      method: "POST",
      body: JSON.stringify(settings),
    });
  }

  sendTestNotificationAsync(): Promise<void> {
    return this.requestAsync<void>("/api/notifications/test", { method: "POST" });
  }

  getSemaNamiSettingsAsync(): Promise<SemaNamiSettingsDto> {
    return this.requestAsync<SemaNamiSettingsDto>("/api/semanami/settings");
  }

  setSemaNamiEnabledAsync(enabled: boolean): Promise<SemaNamiSettingsDto> {
    return this.requestAsync<SemaNamiSettingsDto>("/api/semanami/settings", {
      method: "POST",
      body: JSON.stringify({ enabled }),
    });
  }

  // ─── release endpoints ────────────────────────────────────────────────

  createReleaseAsync(releaseKey: string, workspacePath: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>("/api/releases", {
      method: "POST",
      body: JSON.stringify({ releaseKey, workspacePath }),
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

  switchFeatureAsync(featureId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/features/${featureId}/switch-to`, {
      method: "POST",
    });
  }

  finalizeReleaseAsync(releaseId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}/finalize`, {
      method: "POST",
    });
  }

  setReleaseAutonomousEnabledAsync(releaseId: string, enabled: boolean): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/releases/${releaseId}/autonomous`, {
      method: "PUT",
      body: JSON.stringify({ enabled }),
    });
  }

  startHotfixAsync(key: string, workspacePath: string): Promise<ReleaseFeatureDto> {
    return this.requestAsync<ReleaseFeatureDto>("/api/hotfixes", {
      method: "POST",
      body: JSON.stringify({ key, workspacePath }),
    });
  }

  listHotfixesAsync(workspacePath: string): Promise<ReleaseDto[]> {
    return this.requestAsync<ReleaseDto[]>(`/api/hotfixes?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  finalizeHotfixAsync(hotfixId: string): Promise<ReleaseDto> {
    return this.requestAsync<ReleaseDto>(`/api/hotfixes/${hotfixId}/finalize`, {
      method: "POST",
    });
  }

  // ─── final checks (readiness) ──────────────────────────────────────────

  // The library of strict checks that apply to this project, in plain language.
  getChecksAsync(workspacePath: string): Promise<CheckDefinitionDto[]> {
    return this.requestAsync<CheckDefinitionDto[]>(`/api/checks?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  // Runs the strict checks for a release without shipping it.
  runReadinessAsync(releaseId: string): Promise<ReadinessReportDto> {
    return this.requestAsync<ReadinessReportDto>(`/api/releases/${releaseId}/readiness`, {
      method: "POST",
    });
  }

  getReadinessAsync(releaseId: string): Promise<ReadinessReportDto> {
    return this.requestAsync<ReadinessReportDto>(`/api/releases/${releaseId}/readiness`);
  }

  getReadinessHistoryAsync(releaseId: string): Promise<ReadinessReportDto[]> {
    return this.requestAsync<ReadinessReportDto[]>(`/api/releases/${releaseId}/readiness/history`);
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

  // ─── profiles ───────────────────────────────────────────────────────────

  getProfilesAsync(): Promise<ProfileDto[]> {
    return this.requestAsync<ProfileDto[]>("/api/profiles");
  }

  createProfileAsync(name: string, description: string | null): Promise<ProfileDto> {
    return this.requestAsync<ProfileDto>("/api/profiles", {
      method: "POST",
      body: JSON.stringify({ name, description }),
    });
  }

  updateProfileAsync(
    id: string,
    name: string,
    description: string | null,
    prompts: { stageName: string; promptText: string; overridesBuiltInPrompt: boolean }[],
  ): Promise<ProfileDto> {
    return this.requestAsync<ProfileDto>(`/api/profiles/${id}`, {
      method: "PUT",
      body: JSON.stringify({ name, description, prompts }),
    });
  }

  deleteProfileAsync(id: string): Promise<{ ok: boolean }> {
    return this.requestAsync<{ ok: boolean }>(`/api/profiles/${id}`, { method: "DELETE" });
  }

  setDefaultProfileAsync(id: string): Promise<ProfileDto> {
    return this.requestAsync<ProfileDto>(`/api/profiles/${id}/default`, { method: "POST" });
  }

  getWorkspaceProfileAsync(workspacePath: string): Promise<WorkspaceProfileDto> {
    return this.requestAsync<WorkspaceProfileDto>(`/api/workspace/profile?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  // ─── specialists ────────────────────────────────────────────────────────

  getSpecialistsAsync(): Promise<SpecialistRoleDto[]> {
    return this.requestAsync<SpecialistRoleDto[]>("/api/specialists");
  }

  createSpecialistAsync(name: string, description: string, primingPrompt: string, writesCode: boolean): Promise<SpecialistRoleDto> {
    return this.requestAsync<SpecialistRoleDto>("/api/specialists", {
      method: "POST",
      body: JSON.stringify({ name, description, primingPrompt, writesCode }),
    });
  }

  updateSpecialistAsync(id: string, name: string, description: string, primingPrompt: string, writesCode: boolean): Promise<SpecialistRoleDto> {
    return this.requestAsync<SpecialistRoleDto>(`/api/specialists/${id}`, {
      method: "PUT",
      body: JSON.stringify({ name, description, primingPrompt, writesCode }),
    });
  }

  deleteSpecialistAsync(id: string): Promise<{ ok: boolean }> {
    return this.requestAsync<{ ok: boolean }>(`/api/specialists/${id}`, { method: "DELETE" });
  }

  // ─── pipeline authoring ─────────────────────────────────────────────────

  getWorkspacePipelineAsync(workspacePath: string): Promise<PipelineEditorDto> {
    return this.requestAsync<PipelineEditorDto>(`/api/workspace/pipeline?workspacePath=${encodeURIComponent(workspacePath)}`);
  }

  saveWorkspacePipelineAsync(workspacePath: string, roles: PipelineEditorRoleDto[]): Promise<PipelineEditorDto> {
    return this.requestAsync<PipelineEditorDto>("/api/workspace/pipeline", {
      method: "PUT",
      body: JSON.stringify({ workspacePath, roles }),
    });
  }

  setWorkspaceProfileAsync(workspacePath: string, profileId: string): Promise<WorkspaceProfileDto> {
    return this.requestAsync<WorkspaceProfileDto>("/api/workspace/profile", {
      method: "POST",
      body: JSON.stringify({ workspacePath, profileId }),
    });
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

  // Same checks as runStageGatesAsync, but a failed check is sent back to the agent to fix
  // (a couple of times) before it's handed to the user.
  runStageGatesWithRepairAsync(featureId: string): Promise<RunGatesRepairResultDto> {
    return this.requestAsync<RunGatesRepairResultDto>(`/api/features/${featureId}/run-gates-and-repair`, {
      method: "POST",
    });
  }

  // Selects the model on the stage's live session and verifies it answers before reporting ok.
  // A model that can't be used comes back as { ok: false, message } — not an exception.
  switchStageModelAsync(featureId: string, modelId: string): Promise<ModelSwitchResultDto> {
    return this.requestAsync<ModelSwitchResultDto>(`/api/features/${featureId}/switch-model`, {
      method: "POST",
      body: JSON.stringify({ modelId }),
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

  getRequirementProgressAsync(featureId: string): Promise<RequirementProgressDto> {
    return this.requestAsync<RequirementProgressDto>(`/api/features/${featureId}/progress`);
  }

  getMetricsSummaryAsync(workspacePath: string, days = 30): Promise<MetricsSummaryDto> {
    return this.requestAsync<MetricsSummaryDto>(
      `/api/metrics/summary?workspacePath=${encodeURIComponent(workspacePath)}&days=${days}`,
    );
  }

  getNegotiationAsync(featureId: string): Promise<NegotiationPointDto[]> {
    return this.requestAsync<NegotiationPointDto[]>(`/api/features/${featureId}/negotiation`);
  }

  resolveNegotiationPointAsync(featureId: string, findingId: string): Promise<NegotiationPointDto> {
    return this.requestAsync<NegotiationPointDto>(
      `/api/features/${featureId}/negotiation/${findingId}/resolve`,
      { method: "POST" },
    );
  }

  escalateNegotiationPointAsync(featureId: string, findingId: string): Promise<NegotiationPointDto> {
    return this.requestAsync<NegotiationPointDto>(
      `/api/features/${featureId}/negotiation/${findingId}/escalate`,
      { method: "POST" },
    );
  }

  getCodeContextAsync(workspacePath: string): Promise<CodeContextStatusDto> {
    return this.requestAsync<CodeContextStatusDto>(
      `/api/workspaces/code-context?workspacePath=${encodeURIComponent(workspacePath)}`,
    );
  }

  refreshCodeContextAsync(workspacePath: string): Promise<void> {
    return this.requestAsync<void>(
      `/api/workspaces/code-context/refresh?workspacePath=${encodeURIComponent(workspacePath)}`,
      { method: "POST" },
    );
  }
}