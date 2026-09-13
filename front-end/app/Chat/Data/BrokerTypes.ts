// Mirrors the broker DTOs in DevTeam.Broker/Server/Dtos.cs.

export interface ModelOption {
  value: string;
  name: string;
  description?: string | null;
}

export interface SessionSummary {
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

export interface PromptResponse {
  sessionId: string;
  stopReason: string;
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
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