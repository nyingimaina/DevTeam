// A small, bounded record of recent problems the UI saw — the client half of a support report.
// Persisted so it survives a reload (a user often reloads before thinking to collect anything),
// and deliberately never throws: diagnostics must not be able to break the app.

export interface RecordedError {
  at: string;
  message: string;
  source: "api" | "window" | "rejection";
  url?: string;
  status?: number;
  requestId?: string;
}

const STORAGE_KEY = "devteam.errors";
const LIMIT = 50;

let handlersInstalled = false;

function read(): RecordedError[] {
  if (typeof window === "undefined") return [];
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? (parsed as RecordedError[]) : [];
  } catch {
    return [];
  }
}

function write(entries: RecordedError[]): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(entries));
  } catch {
    // Storage full or unavailable — drop the record rather than surface a new failure.
  }
}

export function recordError(entry: Omit<RecordedError, "at"> & { at?: string }): void {
  const entries = read();
  entries.push({ ...entry, at: entry.at ?? new Date().toISOString() });
  write(entries.slice(-LIMIT));
}

export function recentErrors(): RecordedError[] {
  return read();
}

export function clearErrors(): void {
  write([]);
}

/** Idempotent — safe to call from a component effect on every mount. */
export function installGlobalErrorHandlers(): void {
  if (handlersInstalled || typeof window === "undefined") return;
  handlersInstalled = true;

  window.addEventListener("error", (event) => {
    recordError({ source: "window", message: event.message || "Unknown error" });
  });

  window.addEventListener("unhandledrejection", (event) => {
    const reason = (event as PromiseRejectionEvent).reason;
    recordError({
      source: "rejection",
      message: reason instanceof Error ? reason.message : String(reason),
    });
  });
}
