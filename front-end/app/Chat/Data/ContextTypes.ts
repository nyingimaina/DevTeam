// Mirrors ContextDto / ContextCompactionResult in DevTeam.Broker/Server/Dtos.ts (Dtos.cs).

/**
 * How full the agent's context window is, as of the last reading it reported.
 *
 * The agent only reports its context at the end of a turn, so this is a reading rather than a live
 * gauge: `updatedAt` is how old it is, and `turnActive` says whether a turn is running that has not
 * reported yet. `contextSize` is null when the agent did not report a window size, in which case no
 * percentage can honestly be shown.
 */
export interface ContextDto {
  sessionId: string | null;
  usedTokens: number;
  contextSize: number | null;
  deltaTokens: number | null;
  costAmount: number | null;
  costCurrency: string | null;
  turnActive: boolean;
  compactionCount: number;
  updatedAt: string | null;
}

export interface ContextCompactionResult {
  usedTokensBefore: number;
  usedTokensAfter: number;
  freedTokens: number;
  durationMs: number;
  stopReason: string | null;
  context: ContextDto;
}

/**
 * Occupancy as a fraction, or null when it cannot be computed.
 *
 * Null rather than 0 when the window size is unknown: a bar drawn at 0% is claiming the context was
 * measured and found empty, which is a different statement from "we do not know yet".
 */
export function contextFraction(context: ContextDto | null | undefined): number | null {
  if (!context || context.contextSize == null || context.contextSize <= 0) return null;
  if (context.usedTokens <= 0) return 0;
  return Math.min(1, context.usedTokens / context.contextSize);
}
