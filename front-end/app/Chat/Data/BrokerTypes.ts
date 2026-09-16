// Mirrors the broker DTOs in DevTeam.Broker/Server/Dtos.cs.

export interface ModelOption {
  value: string;
  name: string;
  description?: string | null;
}

export interface PartDto {
  id: string;
  kind: string;
  text?: string | null;
  toolName?: string | null;
  toolCallId?: string | null;
  errorText?: string | null;
  inputJson?: unknown;
  outputJson?: unknown;
  createdAt: string;
}

export interface MessageDto {
  id: string;
  role: string;
  acpMessageId?: string | null;
  providerModelId?: string | null;
  bodyText?: string | null;
  createdAt: string;
  parts: PartDto[];
  isPriming: boolean;
}

export interface SessionDetail {
  sessionId: string;
  acpSessionId: string;
  workspacePath: string;
  title?: string | null;
  modelId?: string | null;
  modeId?: string | null;
  createdAt: string;
  updatedAt: string;
  models: ModelOption[];
  modes: ModelOption[];
  messages: MessageDto[];
}

export interface HealthResponse {
  status: string;
  version?: string | null;
}

export interface AgentViewModel {
  protocolVersion: string;
  agentName: string;
  agentVendor: string;
  agentVersion: string;
  models: ModelOption[];
}

export interface StreamEvent {
  sessionId: string;
  type: string;
  payload: unknown;
  sessionTitle?: string | null;
  at?: string | null;
}

export const StreamEventType = {
  TextDelta: "textDelta",
  ThoughtDelta: "thoughtDelta",
  ToolCall: "toolCall",
  ToolCallUpdated: "toolCallUpdated",
  UsageUpdated: "usageUpdated",
  ConfigOptionsUpdated: "configOptionsUpdated",
  Error: "error",
  TurnEnd: "turnEnd",
} as const;

export interface TextDeltaPayload {
  messageId: string;
  text: string;
}

export interface ThoughtDeltaPayload {
  messageId: string;
  text: string;
}

export interface ToolCallPayload {
  toolCallId: string;
  title?: string | null;
  kind?: string | null;
  status?: string | null;
  rawInput?: unknown;
  rawOutput?: unknown;
}

export interface ConfigOptionsUpdatedPayload {
  options: { configId: string; currentValue: string; options: ModelOption[] }[];
}

export interface TurnEndPayload {
  stopReason: string;
  usage: { inputTokens: number; outputTokens: number; totalTokens: number } | null;
}

export interface FileSystemRootDto {
  path: string;
  displayName: string;
}

export interface FileSystemEntryDto {
  name: string;
  fullPath: string;
  kind: string;
  sizeBytes: number | null;
}

export interface FileSystemStatDto {
  name: string;
  kind: string;
  exists: boolean;
  isGitRepository: boolean;
}

export interface StoppedProcessDto {
  processId: number;
  name: string;
}

export interface ActiveTurnInfo {
  sessionId: string;
  acpSessionId: string;
  preview: string;
  startedAt: string;
}

// ─── release types ─────────────────────────────────────────────────────────

export interface ReleaseDto {
  id: string;
  workspacePath: string;
  title?: string | null;
  version: string;
  status: string;
  branchName: string;
  currentFeatureId?: string | null;
  createdAt: string;
  updatedAt: string;
  features: ReleaseFeatureDto[];
  // Proxy of the current feature's own pipeline state — see DevTeamRelease's
  // computed properties on the backend. Empty/null once no feature is active.
  stageRuns: ReleaseStageRunDto[];
  signoffs: ReleaseSignoffDto[];
  flowPosition?: ReleaseFlowPositionDto | null;
}

export interface ReleaseFeatureDto {
  id: string;
  releaseId: string;
  key: string;
  title: string;
  description?: string | null;
  branchName: string;
  status: string;
  createdAt: string;
  updatedAt: string;
  stageRuns?: ReleaseStageRunDto[];
  signoffs?: ReleaseSignoffDto[];
  flowPosition?: ReleaseFlowPositionDto | null;
}

export interface ReleaseStageRunDto {
  id: string;
  releaseFeatureId: string;
  stageName: string;
  status: string;
  phase: string;
  questionCount: number;
  sessionId?: string | null;
  acpSessionId?: string | null;
  summary?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  attempt: number;
  readyToProceed: boolean;
  gateChecks: ReleaseGateCheckDto[];
  findings: ReviewFindingDto[];
  guidanceNotes: ReleaseGuidanceNoteDto[];
  specialistConsultations: SpecialistConsultationRecordDto[];
  lastErrorKind: string;
  lastErrorMessage?: string | null;
  lastErrorAt?: string | null;
}

export interface SpecialistConsultationRecordDto {
  id: string;
  stageRunId: string;
  specialistName: string;
  question: string;
  responseText: string;
  createdAt: string;
}

export interface ReleaseGuidanceNoteDto {
  id: string;
  stageRunId: string;
  text: string;
  addedBy?: string | null;
  createdAt: string;
}

export interface PipelineStageDto {
  name: string;
  userInputRequired: boolean;
  signoff?: string | null;
  expectedArtifacts: string[];
  steps: string[];
}

export interface StageArtifactDto {
  relativePath: string;
  content?: string | null;
}

export interface ReleaseGateCheckDto {
  id: string;
  stageRunId: string;
  name: string;
  passed: boolean;
  evidenceText?: string | null;
  evidencePath?: string | null;
  completedAt?: string | null;
  isEntryGate: boolean;
  responsibleRole?: string | null;
}

export interface ReleaseSignoffDto {
  id: string;
  releaseFeatureId: string;
  stageName: string;
  required: boolean;
  approved: boolean;
  approvedBy?: string | null;
  comment?: string | null;
  approvedAt?: string | null;
}

export interface ReleaseFlowPositionDto {
  id: string;
  releaseFeatureId: string;
  currentStageIndex: number;
  currentStageName: string;
}

// ─── git types ─────────────────────────────────────────────────────────────

export interface GitStatusDto {
  success: boolean;
  message?: string | null;
  branch?: string | null;
  branches?: string[] | null;
  isRepo: boolean;
  isClean: boolean;
  ahead: number;
  behind: number;
  commits?: GitCommitDto[] | null;
}

export interface GitCommitDto {
  hash: string;
  shortHash: string;
  message: string;
  author: string;
  date: string;
  parents: string[];
  branch?: string | null;
  tags?: string[] | null;
}

export interface GitRemoteDto {
  url?: string | null;
  credentialName?: string | null;
}

export interface GitCredentialsDto {
  names: string[];
}

// ─── stage types ─────────────────────────────────────────────────────────

export interface StageRunDto {
  id: string;
  releaseId: string;
  sessionId?: string | null;
  acpSessionId?: string | null;
  stageName: string;
  status: string;
  phase: string;
  questionCount: number;
  attempt: number;
  startedAt: string;
  finishedAt?: string | null;
  summary?: string | null;
  gateChecks: GateCheckDto[];
  findings: ReviewFindingDto[];
}

export interface GateCheckDto {
  id: string;
  stageRunId: string;
  name: string;
  passed: boolean;
  evidenceText?: string | null;
  evidencePath?: string | null;
  completedAt?: string | null;
}

export interface ReviewFindingDto {
  id: string;
  stageRunId: string;
  target: string;
  kind: string;
  severity: string;
  summary: string;
  status: string;
}

// ─── profiles ───────────────────────────────────────────────────────────────

export interface ProfilePromptDto {
  stageName: string;
  promptText: string;
  overridesBuiltInPrompt: boolean;
}

export interface ProfileDto {
  id: string;
  name: string;
  description: string | null;
  isDefault: boolean;
  prompts: ProfilePromptDto[];
}

export interface WorkspaceProfileDto {
  profileId: string | null;
}

// ─── specialists (Part 3) ─────────────────────────────────────────────────────

export interface SpecialistRoleDto {
  id: string;
  name: string;
  description: string;
  primingPrompt: string;
  writesCode: boolean;
}

// ─── pipeline authoring (Part 2C) ────────────────────────────────────────────

export interface GateStepEditorDto {
  kind: "builtin" | "gatePrompt" | "requiresSpecialist" | "requiresArtifact";
  builtin?: string | null;
  gatePromptText?: string | null;
  responsibleRole?: string | null;
  requiredSpecialist?: string | null;
  requiredArtifactStage?: string | null;
}

// One of ArtifactRoots.All on the backend.
export type ArtifactRootKey = "docs-root" | "feature-docs-root" | "feature-code-root-back" | "feature-code-root-front" | "workspace-root";

export interface ArtifactEditorDto {
  root: ArtifactRootKey;
  fileName: string;
  kind: "text" | "json";
}

export interface PipelineEditorRoleDto {
  name: string;
  writesCode: boolean;
  signoff?: string | null;
  userInputRequired: boolean;
  stepSummary: string[];
  entryGates: GateStepEditorDto[];
  exitGatePrompts: GateStepEditorDto[];
  artifact?: ArtifactEditorDto | null;
}

export interface PipelineEditorDto {
  roles: PipelineEditorRoleDto[];
}

export interface StagePromptResult {
  response: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
}