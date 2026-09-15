import { useEffect, useRef } from "react";
import Poller, { type DeltaCheckArgs } from "jattac-web-poller";

export interface UsePollerOptions<T> {
  /** When false, no Poller is created and nothing is fetched. */
  enabled: boolean;
  func: () => Promise<T>;
  onResult?: (result: T, deltaDetected: boolean) => void | Promise<void>;
  onError?: (error: unknown, consecutiveErrorCount: number) => void;
  hasDelta?: (args: DeltaCheckArgs<T>) => boolean;
  pollIntervalMilliseconds?: number;
  maxIntervalMilliseconds?: number;
  /**
   * A fresh Poller is created whenever these change (e.g. featureId, stageRun.id) —
   * a new resource to poll means a new backoff/delta history, not a continuation of
   * the old one.
   */
  deps: React.DependencyList;
}

/**
 * React lifecycle wrapper around jattac-web-poller's Poller: starts on mount,
 * stops on unmount/dep change. maxConsecutiveErrors is intentionally never set here
 * — retries stay unbounded so a stopped/restarted backend is recovered from
 * automatically instead of the poller giving up forever.
 */
export function usePoller<T>(options: UsePollerOptions<T>): { pollNow: () => void } {
  const funcRef = useRef(options.func);
  funcRef.current = options.func;
  const onResultRef = useRef(options.onResult);
  onResultRef.current = options.onResult;
  const onErrorRef = useRef(options.onError);
  onErrorRef.current = options.onError;
  const hasDeltaRef = useRef(options.hasDelta);
  hasDeltaRef.current = options.hasDelta;

  const pollerRef = useRef<Poller<T> | null>(null);

  useEffect(() => {
    if (!options.enabled) {
      pollerRef.current = null;
      return;
    }

    const poller = new Poller<T>({
      pollIntervalMilliseconds: options.pollIntervalMilliseconds,
      maxIntervalMilliseconds: options.maxIntervalMilliseconds,
      hasDelta: hasDeltaRef.current ? (args) => hasDeltaRef.current!(args) : undefined,
      func: () => funcRef.current(),
      onResult: (result, deltaDetected) => onResultRef.current?.(result, deltaDetected),
      onError: (error, consecutiveErrorCount) => onErrorRef.current?.(error, consecutiveErrorCount),
    });
    pollerRef.current = poller;
    poller.start();

    return () => {
      pollerRef.current = null;
      void poller.stop();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [options.enabled, ...options.deps]);

  return {
    pollNow: () => pollerRef.current?.pollNow(),
  };
}
