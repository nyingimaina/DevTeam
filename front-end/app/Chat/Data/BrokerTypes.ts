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

// One thing the agent did, held in memory for the active turn only (see ActiveTurnTracker).
export interface TurnActivityEntryDto {
  at: string;
  // "tool" | "text" | "thought" | "status"
  kind: string;
  label: string;
  detail?: string | null;
  status?: string | null;
}

export interface ActiveTurnInfo {
  sessionId: string;
  acpSessionId: string;
  preview: string;
  startedAt: string;
  // True when the prompt was composed by DevTeam rather than typed by a person — the preview
  // is then boilerplate ("You are the developer for feature…"), so it is not a useful label.
  isPriming?: boolean;
  // The DevTeamSession id, which is what ReleaseStageRun.acpSessionId actually stores. Use
  // THIS to correlate the active turn to a stage run — never acpSessionId (the real ACP id).
  // Optional: older brokers omit it, so the UI falls back to sessionId.
  stageRunSessionId?: string;
  // When the agent last produced anything — the honest "is it still working?" signal.
  lastEventAt?: string | null;
  // Most recent first-class activity, oldest first. In memory only; empty after a refresh.
  activity?: TurnActivityEntryDto[] | null;
  // Prompts queued for the broker's single turn slot, this one included.
  queuedTurns?: number;
}

// ─── release types ─────────────────────────────────────────────────────────

export interface ReleaseDto {
  id: string;
  workspacePath: string;
  title?: string | null;
  version: string;
  status: string;
  // Derived by the broker from the features (a stored "Ready" goes stale when a feature is
  // added). Prefer this for display and gating — see effectiveReleaseStatus.
  effectiveStatus?: string;
  branchName: string;
  currentFeatureId?: string | null;
  // True for a hotfix's release-shell (Part 7F) — reuses the exact same wire shape as a
  // normal release, branched from main instead of develop and excluded from the normal
  // release list.
  isHotfix?: boolean;
  // Persisted "Continue automatically" state (CruiseControl) — survives a reload instead of
  // resetting to a manual click every time the page is revisited.
  autonomousEnabled?: boolean;
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
  /** How many times in a row this stage has failed the same checks. */
  consecutiveFailures?: number;
  /** True once the same check has failed too many times — stop auto-retrying and ask a person. */
  autoRetrySuppressed?: boolean;
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
  // Plain-language label for each entry in `steps`, same order — what the UI shows. `steps`
  // stays the stable identifier used for testids/diagnostics. Optional so the UI degrades to
  // the raw id rather than breaking if the broker predates this field.
  stepLabels?: string[];
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
  // Plain-language text computed by the broker; fall back to `name`/`evidenceText` if absent.
  displayTitle?: string;
  plainProblem?: string;
}

export interface GateProblemDto {
  gateName: string;
  title: string;
  whatWentWrong: string;
  technicalDetail: string;
}

// Result of trying another AI model on a stage the provider refused: `ok` only once the model
// actually answered a connection check. `message` is plain language, safe to show as-is.
export interface ModelSwitchResultDto {
  ok: boolean;
  message: string;
  modelId?: string | null;
}

export interface RunGatesRepairResultDto {
  release: ReleaseDto;
  outcome: "Passed" | "NeedsYou";
  autoFixAttempts: number;
  problems: GateProblemDto[];
}

// ─── final checks (readiness) ──────────────────────────────────────────
// The numbers a run produced — used for the cards and the trend lines.
export interface ReadinessMetricsDto {
  testsPassed?: number | null;
  testsFailed?: number | null;
  testsSkipped?: number | null;
  lineCoverage?: number | null;
  branchCoverage?: number | null;
  functionCoverage?: number | null;
}

export interface ReadinessCheckDto {
  phaseId: string;
  title: string;
  status: "Passed" | "Failed" | "Skipped";
  reason: string;
  durationMs: number;
  metrics: ReadinessMetricsDto;
  rawOutput?: string | null;
}

export interface ReadinessReportDto {
  id: string;
  releaseId?: string | null;
  featureId?: string | null;
  scope: string;
  passed: boolean;
  startedAt: string;
  durationMs: number;
  releaseVersion?: string | null;
  checks: ReadinessCheckDto[];
  failedCount: number;
  skippedCount: number;
  // Plain language, computed by the broker — safe to show a person as-is.
  blockerSummary: string;
}

// One entry in the Checks library: what a check is, why it matters, and how to fix it.
export interface CheckDefinitionDto {
  id: string;
  title: string;
  category: string;
  whyItMatters: string;
  howToFix: string;
  required: boolean;
  scope: string;
  technical: string;
}

// ─── diagnostics (support hand-off) ────────────────────────────────────────
export interface DiagnosticsSettingsDto {
  // True while detailed logging is being captured for a support investigation.
  verboseLogging: boolean;
  // Where the log files live on this machine — shown to the user so they can also look.
  logsDirectory: string;
}

// One entry in a project's model list. Cost/Smartness are read-only curated estimates, null for
// a model we have no estimate for (shown as "unknown").
export interface ModelCandidateDto {
  id: string;
  modelId: string;
  priority: number;
  enabled: boolean;
  userAdded: boolean;
  cooldownUntil?: string | null;
  lastFailureKind?: string | null;
  lastFailureReason?: string | null;
  cost?: number | null;
  smartness?: number | null;
  note?: string | null;
}

export interface NotificationSettingsDto {
  stageComplete: boolean;
  needsAttention: boolean;
  approvalNeeded: boolean;
  sound: boolean;
}

export interface SemaNamiSettingsDto {
  enabled: boolean;
  // Whether TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID are actually set — the toggle can still be
  // flipped on when this is false, it just won't do anything until the env vars are set too.
  available: boolean;
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
  seedPrompt?: string | null;
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

export type CodeContextState = "None" | "UpToDate" | "Behind" | "Refreshing" | "Unavailable";

export interface CodeContextStatusDto {
  state: CodeContextState;
  changesBehind?: number | null;
  builtAt?: string | null;
  warnings: string[];
}

export interface MetricsTotalsDto {
  turns: number;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  cachedReadTokens: number;
  durationMs: number;
  costAmount?: number | null;
}

export interface StageMetricsDto {
  stageName: string;
  turns: number;
  attempts: number;
  totalTokens: number;
  retryTokens: number;
  challengeTokens: number;
  durationMs: number;
  gateFailures: number;
}

export interface KindMetricsDto {
  kind: string;
  turns: number;
  totalTokens: number;
  durationMs: number;
}

export interface ModelMetricsDto {
  modelId: string;
  turns: number;
  totalTokens: number;
  cachedReadTokens: number;
  durationMs: number;
}

export interface PromptSectionMetricsDto {
  section: string;
  totalChars: number;
  avgChars: number;
  percentOfPrompt: number;
}

export interface MetricsFindingDto {
  id: string;
  severity: string;
  title: string;
  evidence: Record<string, unknown>;
  suggestedAction: string;
}

export interface MetricsSummaryDto {
  schemaVersion: number;
  generatedAtUtc: string;
  scope: { workspacePath?: string | null; featureId?: string | null; releaseId?: string | null; days: number };
  totals: MetricsTotalsDto;
  perStage: StageMetricsDto[];
  perKind: KindMetricsDto[];
  perModel: ModelMetricsDto[];
  promptSections: PromptSectionMetricsDto[];
  findings: MetricsFindingDto[];
  notes: string[];
}

export interface ProgressCountDto {
  done: number;
  total: number;
}

export interface RequirementProgressDto {
  requirements: number;
  code: ProgressCountDto;
  tests: ProgressCountDto;
}

export type NegotiationPointStatus = "Open" | "Resolved" | "Escalated";
export type NegotiationResponseKind = "None" | "Addressed" | "Disputed" | "Blocked";

export interface NegotiationPointDto {
  id: string;
  target: string;
  summary: string;
  expected?: string | null;
  round: number;
  openedBy?: string | null;
  pushedBackTo?: string | null;
  status: NegotiationPointStatus;
  responseKind: NegotiationResponseKind;
  responseText?: string | null;
  requirementRef?: string | null;
  createdAt: string;
}