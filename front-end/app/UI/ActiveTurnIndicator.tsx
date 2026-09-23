import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../Chat/Data/BrokerApi";
import { ActiveTurnInfo } from "../Chat/Data/BrokerTypes";
import { usePoller } from "../Chat/Data/usePoller";
import { formatElapsed } from "./formatElapsed";
import styles from "./ActiveTurnIndicator.module.css";

const POLL_MS = 4000;

interface IActiveTurnIndicatorProps {
  api: BrokerApi;
}

// The broker drives one shared agent process for every release, so at most one
// turn can be running (or stuck) at a time — this shows whichever one that is,
// regardless of which project is currently open, since a stuck turn from any
// release blocks every other release until it's cancelled.
export default function ActiveTurnIndicator({ api }: IActiveTurnIndicatorProps) {
  const [turn, setTurn] = useState<ActiveTurnInfo | undefined>(undefined);
  const [, forceTick] = useState(0);
  const [cancelling, setCancelling] = useState(false);

  // maxConsecutiveErrors is intentionally left unset — a transient poll failure
  // shouldn't clear a currently-shown turn, and the poller keeps retrying rather
  // than ever giving up.
  const { pollNow } = usePoller<ActiveTurnInfo | undefined>({
    enabled: true,
    func: () => api.getCurrentTurnAsync(),
    onResult: (current) => setTurn(current),
    pollIntervalMilliseconds: POLL_MS,
    deps: [api],
  });

  useEffect(() => {
    if (!turn) return;
    const timer = window.setInterval(() => forceTick((t) => t + 1), 1000);
    return () => window.clearInterval(timer);
  }, [turn]);

  const handleCancel = useCallback(async () => {
    setCancelling(true);
    try {
      await api.cancelCurrentTurnAsync();
      pollNow();
    } finally {
      setCancelling(false);
    }
  }, [api, pollNow]);

  if (!turn) return null;

  // A composed prompt (isPriming) reads as boilerplate ("You are the developer for feature…"),
  // so it is not a useful label — say what it is instead. A message the person actually typed
  // is worth showing back to them verbatim.
  const label = turn.isPriming ? "An agent step is running" : turn.preview;

  return (
    <div className={styles.indicator} role="status">
      <span className={styles.preview} title={turn.preview}>{label}</span>
      <span className={styles.elapsed}>{formatElapsed(turn.startedAt)}</span>
      <button type="button" className={styles.cancel} onClick={() => void handleCancel()} disabled={cancelling}>
        {cancelling ? "Cancelling…" : "Cancel"}
      </button>
    </div>
  );
}
