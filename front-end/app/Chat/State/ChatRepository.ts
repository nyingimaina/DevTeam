import BrokerApi from "../Data/BrokerApi";
import { MessageDto, SessionDetail, SessionSummary } from "../Data/BrokerTypes";
import BrokerHub from "../Data/BrokerHub";

export interface LiveAssistant {
  messageId?: string;
  text: string;
  toolCalls: { toolCallId: string; title?: string | null; kind?: string | null }[];
}

export default class ChatRepository {
  busy = false;
  brokerReady = false;
  brokerVersion?: string;
  agentName?: string;
  sessions: SessionSummary[] = [];
  activeSession?: SessionDetail;
  liveAssistant?: LiveAssistant;
  error?: string;
  isPending = false;
  currentModelId?: string;
  currentModeId?: string;
}

export type OnErrorChanged = (error: string | undefined) => void;