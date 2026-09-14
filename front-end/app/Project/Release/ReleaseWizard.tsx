import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import {
  MessageDto,
  PipelineStageDto,
  ReleaseDto,
  ReleaseStageRunDto,
  ReviewFindingDto,
} from "../../Chat/Data/BrokerTypes";
import MessageRow from "../../Chat/UI/MessageRow";
import ZestButton from "jattac.libs.web.zest-button";
import { phaseLabel, stageLabel, statusLabel, whatsNext } from "./labels";
import styles from "../Styles/ReleaseWizard.module.css";

interface IReleaseWizardProps {
  api: BrokerApi;
  workspacePath: string;
  testIdPrefix?: string;
}

type WizardView = "list" | "create" | "detail";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function statusColor(status: string): string {
  switch (status) {
    case "Complete": return styles.statusComplete;
    case "InProgress": return styles.statusInProgress;
    case "Blocked": return styles.statusBlocked;
    case "BlockedGate": return styles.statusBlocked;
    case "BlockedSignoff": return styles.statusBlocked;
    case "Ready": return styles.statusReady;
    default: return "";
  }
}

function isRunning(status: string): boolean {
  return status === "Active" || status === "Producing" || status === "Gates" || status === "Challenge" || status === "Signoff";
}

function latestRunFor(release: ReleaseDto, stageName: string): ReleaseStageRunDto | undefined {
  const runs = release.stageRuns.filter((sr) => sr.stageName === stageName);
  if (runs.length === 0) return undefined;
  return [...runs].sort((a, b) => (b.startedAt ?? "").localeCompare(a.startedAt ?? ""))[0];
}

export default function ReleaseWizard({ api, workspacePath, testIdPrefix = "release" }: IReleaseWizardProps) {
  const [view, setView] = useState<WizardView>("list");
  const [releases, setReleases] = useState<ReleaseDto[]>([]);
  const [selectedRelease, setSelectedRelease] = useState<ReleaseDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [featureKey, setFeatureKey] = useState("");

  const loadReleases = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const list = await api.listReleasesAsync();
      setReleases(list);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api]);

  useEffect(() => {
    if (view === "list") loadReleases();
  }, [view, loadReleases]);

  const handleCreate = useCallback(async () => {
    if (!featureKey.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const release = await api.createReleaseAsync(featureKey.trim(), workspacePath);
      setSelectedRelease(release);
      setView("detail");
      setFeatureKey("");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, featureKey, workspacePath]);

  const handleSelectRelease = useCallback(async (releaseId: string) => {
    setLoading(true);
    setError(null);
    try {
      const release = await api.getReleaseAsync(releaseId);
      setSelectedRelease(release);
      setView("detail");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api]);

  const handleRefresh = useCallback(async () => {
    if (!selectedRelease) return;
    setLoading(true);
    setError(null);
    try {
      const updated = await api.getReleaseAsync(selectedRelease.id);
      setSelectedRelease(updated);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, selectedRelease]);

  return (
    <div className={styles.container} data-testid={testIdPrefix}>
      {error && <div className={styles.error}>{error}</div>}

      {view === "list" && (
        <div className={styles.listView}>
          <div className={styles.header}>
            <h2>Releases</h2>
            <ZestButton
              type="button"
              onClick={() => setView("create")}
              disabled={loading}
              zest={{ semanticType: "add", visualOptions: { size: "sm" } }}
            >
              New Release
            </ZestButton>
          </div>
          {loading && <div className={styles.loading}>Loading...</div>}
          <div className={styles.releaseList}>
            {releases.map((r) => (
              <div
                key={r.id}
                className={styles.releaseItem}
                onClick={() => handleSelectRelease(r.id)}
                data-testid={`${testIdPrefix}-item-${r.id}`}
              >
                <span className={styles.releaseTitle}>{r.title ?? r.features[0]?.key ?? r.id.slice(0, 8)}</span>
                <span className={`${styles.releaseStatus} ${statusColor(r.status)}`}>{statusLabel(r.status)}</span>
                <span className={styles.releaseStages}>
                  {r.flowPosition?.currentStageName ? stageLabel(r.flowPosition.currentStageName) : "—"}
                </span>
              </div>
            ))}
            {releases.length === 0 && !loading && <div className={styles.empty}>No releases yet.</div>}
          </div>
        </div>
      )}

      {view === "create" && (
        <div className={styles.createView}>
          <div className={styles.header}>
            <h2>New Release</h2>
            <ZestButton type="button" onClick={() => setView("list")} disabled={loading}
              zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}>Back</ZestButton>
          </div>
          <div className={styles.form}>
            <label>
              Feature Key
              <input
                value={featureKey}
                onChange={(e) => setFeatureKey(e.target.value)}
                placeholder="e.g. login-form"
                data-testid={`${testIdPrefix}-feature-key`}
              />
            </label>
            <ZestButton
              type="button"
              onClick={handleCreate}
              disabled={!featureKey.trim()}
              data-testid={`${testIdPrefix}-create-btn`}
              zest={{ semanticType: "save", busyOptions: { preventRageClick: true } }}
            >
              {loading ? "Creating..." : "Create Release"}
            </ZestButton>
          </div>
        </div>
      )}

      {view === "detail" && selectedRelease && (
        <ReleaseDetail
          release={selectedRelease}
          api={api}
          testIdPrefix={testIdPrefix}
          loading={loading}
          onBack={() => { setView("list"); setSelectedRelease(null); }}
          onRefresh={handleRefresh}
          onReleaseUpdated={setSelectedRelease}
        />
      )}
    </div>
  );
}

// ─── release detail with per-stage screens ─────────────────────────────────

interface IReleaseDetailProps {
  release: ReleaseDto;
  api: BrokerApi;
  testIdPrefix: string;
  loading: boolean;
  onBack: () => void;
  onRefresh: () => void;
  onReleaseUpdated: (release: ReleaseDto) => void;
}

function ReleaseDetail({ release, api, testIdPrefix, loading, onBack, onRefresh, onReleaseUpdated }: IReleaseDetailProps) {
  const [pipeline, setPipeline] = useState<PipelineStageDto[]>([]);
  const [pipelineError, setPipelineError] = useState<string | null>(null);

  const loadPipeline = useCallback(async () => {
    setPipelineError(null);
    try {
      const p = await api.getPipelineAsync(release.id);
      setPipeline(p);
    } catch (e) {
      setPipelineError(toErrorMessage(e));
    }
  }, [api, release.id]);

  useEffect(() => {
    void loadPipeline();
  }, [loadPipeline]);

  const refreshRelease = useCallback(async () => {
    try {
      const fresh = await api.getReleaseAsync(release.id);
      onReleaseUpdated(fresh);
    } catch (e) {
      setPipelineError(toErrorMessage(e));
    }
  }, [api, release.id, onReleaseUpdated]);

  const stageIndex = useMemo(() => {
    if (pipeline.length === 0) return release.flowPosition?.currentStageIndex ?? 0;
    const fp = release.flowPosition;
    if (release.status === "Ready" || release.status === "Complete") return pipeline.length;
    if (fp && fp.currentStageIndex >= 0 && fp.currentStageIndex < pipeline.length) return fp.currentStageIndex;
    if (fp) return Math.min(fp.currentStageIndex, pipeline.length - 1);
    return 0;
  }, [pipeline, release]);

  const role = stageIndex < pipeline.length ? pipeline[stageIndex] : null;
  const run = role ? latestRunFor(release, role.name) : undefined;
  const currentRunId = run?.id;

  useEffect(() => {
    if (!run || !role || role.userInputRequired) return;
    if (!isRunning(run.status)) return;
    const timer = window.setInterval(() => {
      void refreshRelease();
    }, 2000);
    return () => window.clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, release.id, currentRunId, run?.status, role?.userInputRequired]);

  const pendingSignoffs = release.signoffs.filter((s) => s.required && !s.approved);

  return (
    <div className={styles.detailView}>
      <div className={styles.header}>
        <ZestButton type="button" onClick={onBack} disabled={loading}
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}>Back</ZestButton>
        <h2>{release.title ?? release.features[0]?.key}</h2>
        <span className={`${styles.releaseStatus} ${statusColor(release.status)}`}>{statusLabel(release.status)}</span>
        <ZestButton type="button" onClick={onRefresh} disabled={loading}
          zest={{ semanticType: "refresh", busyOptions: { preventRageClick: true }, buttonStyle: "text", visualOptions: { size: "sm" } }}>
          Refresh
        </ZestButton>
      </div>

      <details className={styles.advancedDetails}>
        <summary>Advanced details</summary>
        <div className={styles.detailInfo}>
          <span>ID: {release.id.slice(0, 8)}...</span>
          <span>Workspace: {release.workspacePath}</span>
          <span>Version: {release.version}</span>
        </div>
      </details>

      {pipeline.length > 0 && (
        <PipelineStepper pipeline={pipeline} stageIndex={stageIndex} signoffs={release.signoffs} />
      )}

      {pipelineError && <div className={styles.error}>{pipelineError}</div>}

      <StageScreen
        release={release}
        api={api}
        pipeline={pipeline}
        role={role}
        run={run}
        testIdPrefix={testIdPrefix}
        refreshRelease={refreshRelease}
      />

      {pendingSignoffs.length > 0 && (
        <div className={styles.signoffSection}>
          <h3>Your review</h3>
          <p className={styles.signoffIntro}>
            The {stageLabel(pendingSignoffs[0].stageName)} stage finished and needs your signoff before moving on.
          </p>
          {pendingSignoffs.map((s) => (
            <SignoffButton
              key={s.stageName}
              release={release}
              signoff={s}
              api={api}
              testIdPrefix={testIdPrefix}
              onReleaseUpdated={onReleaseUpdated}
            />
          ))}
        </div>
      )}

      {release.stageRuns.length > 0 && (
        <details className={styles.timeline} open>
          <summary>Stage history</summary>
          {release.stageRuns.map((sr) => (
            <StageHistoryCard key={sr.id} stageRun={sr} testIdPrefix={testIdPrefix} />
          ))}
        </details>
      )}
    </div>
  );
}

// ─── pipeline stepper ──────────────────────────────────────────────────────

function PipelineStepper({
  pipeline,
  stageIndex,
  signoffs,
}: {
  pipeline: PipelineStageDto[];
  stageIndex: number;
  signoffs: ReleaseDto["signoffs"];
}) {
  return (
    <div className={styles.stepper} data-testid="pipeline-stepper">
      {pipeline.map((p, i) => {
        let cls = styles.stepChip;
        if (i < stageIndex) cls += ` ${styles.stepChipDone}`;
        else if (i === stageIndex) cls += ` ${styles.stepChipCurrent}`;
        else cls += ` ${styles.stepChipPending}`;
        const approvedSignoff = signoffs.some((s) => s.required && s.approved && s.stageName === p.signoff);
        return (
          <React.Fragment key={p.name}>
            {i > 0 && <span className={styles.stepSeparator}>›</span>}
            <span className={cls} data-testid={`pipeline-step-${p.name}`}>
              {i < stageIndex && <span className={styles.stepTick}>✓ </span>}
              {i + 1} {stageLabel(p.name)}
              {p.signoff && <span className={styles.stepSignoff}>{approvedSignoff ? " · approved" : " · review needed"}</span>}
            </span>
          </React.Fragment>
        );
      })}
    </div>
  );
}

// ─── stage screen (dedicated view for the current stage) ───────────────────

interface IStageScreenProps {
  release: ReleaseDto;
  api: BrokerApi;
  pipeline: PipelineStageDto[];
  role: PipelineStageDto | null;
  run: ReleaseStageRunDto | undefined;
  testIdPrefix: string;
  refreshRelease: () => Promise<void>;
}

function StageScreen({ release, api, pipeline, role, run, testIdPrefix, refreshRelease }: IStageScreenProps) {
  const [busy, setBusy] = useState(false);
  const [stageError, setStageError] = useState<string | null>(null);

  const handleStartStage = useCallback(async () => {
    setBusy(true);
    setStageError(null);
    try {
      await api.startStageAsync(release.id);
      await refreshRelease();
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }, [api, release.id, refreshRelease]);

  const handleRunStage = useCallback(async () => {
    setBusy(true);
    setStageError(null);
    try {
      await api.runStageAsync(release.id);
      await refreshRelease();
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }, [api, release.id, refreshRelease]);

  if (release.status === "Ready" || release.status === "Complete" || !role) {
    return (
      <div className={styles.stagePanel}>
        <div className={styles.doneBanner}>
          <strong>Release complete.</strong> All stages finished successfully.
        </div>
      </div>
    );
  }

  const interactive = role.userInputRequired;

  return (
    <div className={styles.stagePanel}>
      <div className={styles.stageHeader}>
        <span className={styles.stageName}>Current Stage: {stageLabel(role.name)}</span>
        {run && (
          <>
            <span className={styles.phaseBadge}>{phaseLabel(run.phase)}</span>
            <span className={styles.questionBadge}>attempt {run.attempt}</span>
          </>
        )}
      </div>
      {whatsNext(role.name) && <div className={styles.whatsNext}>{whatsNext(role.name)}</div>}

      {stageError && <div className={styles.error}>{stageError}</div>}

      {!run && (
        <div className={styles.noRun}>
          <div className={styles.noRunHint}>
            {interactive
              ? "This stage runs as a conversation with the agent. Start it to begin the dialogue."
              : "This stage runs autonomously in the workspace. Start it and watch the log below."}
          </div>
          <ZestButton
            type="button"
            onClick={interactive ? handleStartStage : handleRunStage}
            disabled={busy}
            data-testid={`${testIdPrefix}-start-stage-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true } }}
          >
            {busy ? "Starting..." : interactive ? "Start Conversation" : "Run Stage"}
          </ZestButton>
        </div>
      )}

      {run && interactive && (
        <ChatStage
          key={run.id}
          release={release}
          stageRun={run}
          api={api}
          testIdPrefix={testIdPrefix}
          busy={busy}
          runStage={handleRunStage}
          refreshRelease={refreshRelease}
        />
      )}

      {run && !interactive && (
        <StageLog
          key={run.id}
          release={release}
          stageRun={run}
          api={api}
          testIdPrefix={testIdPrefix}
          busy={busy}
          runStage={handleRunStage}
          refreshRelease={refreshRelease}
        />
      )}

      {run?.status === "BlockedGate" && pipeline.length > 0 && (
        <PushBackPanel
          release={release}
          api={api}
          stageIndex={release.flowPosition?.currentStageIndex ?? 0}
          pipeline={pipeline}
          testIdPrefix={testIdPrefix}
          refreshRelease={refreshRelease}
        />
      )}
    </div>
  );
}

// ─── interactive chat stage (mirrors the Chat tab for this stage) ──────────

interface IChatStageProps {
  release: ReleaseDto;
  stageRun: ReleaseStageRunDto;
  api: BrokerApi;
  testIdPrefix: string;
  busy: boolean;
  runStage: () => void;
  refreshRelease: () => Promise<void>;
}

function ChatStage({ release, stageRun, api, testIdPrefix, busy, runStage, refreshRelease }: IChatStageProps) {
  const [messages, setMessages] = useState<MessageDto[]>([]);
  const [input, setInput] = useState("");
  const [sending, setSending] = useState(false);
  const [gatesRunning, setGatesRunning] = useState(false);
  const [messagesError, setMessagesError] = useState<string | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);
  const gatesInFlight = useRef(false);
  const lastAutoRunMsgId = useRef<string | null>(null);

  const hasStandaloneDone = useCallback((text: string): boolean => {
    return text.split("\n").some((line) => /^\s*done\s*$/i.test(line));
  }, []);

  const autoRunGates = useCallback(async (msgs: MessageDto[]) => {
    if (gatesInFlight.current) return;
    const latest = [...msgs].reverse().find((m) => m.role === "assistant" && m.bodyText);
    if (!latest || !latest.bodyText) return;
    if (latest.id === lastAutoRunMsgId.current) return;
    if (!hasStandaloneDone(latest.bodyText)) return;
    lastAutoRunMsgId.current = latest.id;
    gatesInFlight.current = true;
    setGatesRunning(true);
    try {
      await api.runStageGatesAsync(release.id);
      await refreshRelease();
    } catch (e) {
      setMessagesError(toErrorMessage(e));
    } finally {
      gatesInFlight.current = false;
      setGatesRunning(false);
    }
  }, [api, release.id, hasStandaloneDone, refreshRelease]);

  const loadMessages = useCallback(async () => {
    try {
      const msgs = await api.getStageMessagesAsync(release.id, stageRun.id);
      setMessages(msgs);
      void autoRunGates(msgs);
    } catch (e) {
      setMessagesError(toErrorMessage(e));
    }
  }, [api, release.id, stageRun.id, autoRunGates]);

  useEffect(() => {
    let cancelled = false;
    let timer: number | undefined;
    const tick = async () => {
      try {
        const msgs = await api.getStageMessagesAsync(release.id, stageRun.id);
        if (!cancelled) {
          setMessages(msgs);
          setMessagesError(null);
          void autoRunGates(msgs);
        }
      } catch (e) {
        if (!cancelled) setMessagesError(toErrorMessage(e));
      }
    };
    if (isRunning(stageRun.status) || stageRun.status === "BlockedSignoff" || stageRun.status === "BlockedGate") {
      void tick();
      timer = window.setInterval(tick, 2500);
    }
    return () => {
      cancelled = true;
      if (timer !== undefined) window.clearInterval(timer);
    };
  }, [api, release.id, stageRun.id, stageRun.status, autoRunGates]);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView?.({
      block: "nearest",
    });
  }, [messages]);

  const handleSend = useCallback(async () => {
    const text = input.trim();
    if (!text || sending) return;
    setInput("");
    setMessages((prev) => [
      ...prev,
      { id: `pending-${Date.now()}`, role: "user", bodyText: text, createdAt: new Date().toISOString(), parts: [] },
    ]);
    setSending(true);
    setMessagesError(null);
    try {
      await api.sendStageMessageAsync(release.id, text);
      await loadMessages();
    } catch (e) {
      setMessagesError(toErrorMessage(e));
    } finally {
      setSending(false);
    }
  }, [api, release.id, input, sending, loadMessages]);

  return (
    <div className={styles.chatPanel}>
      {messagesError && <div className={styles.error}>{messagesError}</div>}
      <div className={styles.chatContainer}>
        {messages.length === 0 && (
          <div className={styles.chatIntro}>
            Conversation with the {stageRun.stageName} agent will appear here.
            {stageRun.status === "BlockedSignoff" && " Gates are done — awaiting signoff above."}
          </div>
        )}
        {messages.map((message) => (
          <MessageRow key={message.id} message={message} />
        ))}
        {sending && (
          <div className={styles.thinkingRow} role="status" data-testid={`${testIdPrefix}-thinking`}>
            <div className={styles.thinkingBubble}>thinking…</div>
          </div>
        )}
        {gatesRunning && (
          <div className={styles.thinkingRow} role="status" data-testid={`${testIdPrefix}-gates`}>
            <div className={styles.thinkingBubble}>running gates…</div>
          </div>
        )}
        <div ref={messagesEndRef} />
      </div>

      <div className={styles.chatInput}>
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") void handleSend(); }}
          placeholder="Message the agent…"
          className={styles.input}
          disabled={sending || busy || gatesRunning}
          data-testid={`${testIdPrefix}-chat-input`}
        />
        <ZestButton
          type="button"
          onClick={() => void handleSend()}
          disabled={sending || busy || gatesRunning || !input.trim()}
          data-testid={`${testIdPrefix}-send-btn`}
          zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
        >
          Send
        </ZestButton>
      </div>
    </div>
  );
}

// ─── autonomous stage log ──────────────────────────────────────────────────

interface IStageLogProps {
  release: ReleaseDto;
  stageRun: ReleaseStageRunDto;
  api: BrokerApi;
  testIdPrefix: string;
  busy: boolean;
  runStage: () => void;
  refreshRelease: () => Promise<void>;
}

function StageLog({ stageRun, testIdPrefix, busy, runStage, refreshRelease }: IStageLogProps) {
  const logEndRef = useRef<HTMLDivElement>(null);

  const lines = useMemo(() => {
    const out: { text: string; tone: "phase" | "ok" | "warn" | "err" | "plain" }[] = [
      { text: `attempt ${stageRun.attempt} started — ${phaseLabel(stageRun.phase)}`, tone: "phase" },
    ];

    for (const note of stageRun.guidanceNotes ?? []) {
      out.push({ text: `rework note: ${note.text}`, tone: "warn" });
    }

    for (const gc of stageRun.gateChecks) {
      out.push({
        text: `${gc.passed ? "✓" : "✗"} gate ${gc.name}${gc.evidenceText ? ` — ${gc.evidenceText}` : ""}`,
        tone: gc.passed ? "ok" : "err",
      });
    }

    for (const f of stageRun.findings) {
      out.push({ text: `⚠ ${f.severity} ${f.target}: ${f.summary}`, tone: f.severity === "Blocker" ? "err" : "warn" });
    }

    if (stageRun.status === "BlockedGate") {
      out.push({ text: "stage blocked — gates failed. Review findings and push back for rework.", tone: "err" });
    } else if (stageRun.status === "BlockedSignoff") {
      out.push({ text: "stage blocked — signoff required to continue.", tone: "warn" });
    } else if (stageRun.status === "Complete") {
      out.push({ text: "stage complete.", tone: "ok" });
    } else {
      out.push({ text: `${phaseLabel(stageRun.phase)}…`, tone: "phase" });
    }
    return out;
  }, [stageRun]);

  useEffect(() => {
    logEndRef.current?.scrollIntoView?.({
      block: "nearest",
    });
  }, [lines]);

  const showRunButton = isRunning(stageRun.status) || stageRun.status === "BlockedGate";
  const showRefreshButton = isRunning(stageRun.status);

  return (
    <div className={styles.logPanel}>
      <div className={styles.logContainer} data-testid={`${testIdPrefix}-stage-log`}>
        {lines.map((line, i) => (
          <div
            key={i}
            className={`${styles.logLine} ${line.tone === "phase" ? styles.logPhase : line.tone === "ok" ? styles.logLineOk : line.tone === "err" ? styles.logLineBad : line.tone === "warn" ? styles.logLineWarn : ""}`}
          >
            {line.text}
          </div>
        ))}
        <div ref={logEndRef} />
      </div>

      <div className={styles.logActions}>
        {showRunButton && (
          <ZestButton
            type="button"
            onClick={runStage}
            disabled={busy}
            data-testid={`${testIdPrefix}-run-stage-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
          >
            {busy ? "Running…" : isRunning(stageRun.status) ? "Run Stage Again" : "Run Stage"}
          </ZestButton>
        )}
        {showRefreshButton && (
          <ZestButton
            type="button"
            onClick={() => void refreshRelease()}
            disabled={busy}
            data-testid={`${testIdPrefix}-refresh-log-btn`}
            zest={{ semanticType: "refresh", busyOptions: { preventRageClick: true }, buttonStyle: "text", visualOptions: { size: "sm" } }}
          >
            Refresh
          </ZestButton>
        )}
      </div>
    </div>
  );
}

// ─── push-back panel ───────────────────────────────────────────────────────

interface IPushBackPanelProps {
  release: ReleaseDto;
  api: BrokerApi;
  stageIndex: number;
  pipeline: PipelineStageDto[];
  testIdPrefix: string;
  refreshRelease: () => Promise<void>;
}

function PushBackPanel({ release, api, stageIndex, pipeline, testIdPrefix, refreshRelease }: IPushBackPanelProps) {
  const [target, setTarget] = useState(pipeline[Math.max(0, stageIndex - 1)]?.name ?? "");
  const [instructions, setInstructions] = useState("");
  const [pushing, setPushing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const targets = pipeline.slice(0, stageIndex).map((p) => p.name);

  useEffect(() => {
    if (!target && targets.length > 0) setTarget(targets[targets.length - 1]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const handlePushBack = useCallback(async () => {
    if (!target || !instructions.trim()) return;
    setPushing(true);
    setError(null);
    try {
      await api.pushBackAsync(release.id, target, instructions.trim());
      setInstructions("");
      await refreshRelease();
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setPushing(false);
    }
  }, [api, release.id, target, instructions, refreshRelease]);

  if (targets.length === 0) return null;

  return (
    <div className={styles.pushBack}>
      {error && <div className={styles.error}>{error}</div>}
      <div className={styles.pushBackTitle}>Push back to an earlier stage for rework</div>
      <label className={styles.pushBackLabel}>
        Target stage
        <select
          value={target}
          onChange={(e) => setTarget(e.target.value)}
          className={styles.pushBackSelect}
          data-testid={`${testIdPrefix}-pushback-target`}
        >
          {targets.map((name) => (
            <option key={name} value={name}>{name}</option>
          ))}
        </select>
      </label>
      <label className={styles.pushBackLabel}>
        Instructions for the rework
        <textarea
          value={instructions}
          onChange={(e) => setInstructions(e.target.value)}
          placeholder="e.g. The login form is missing client-side validation. Add it and re-run the tests."
          className={styles.pushBackTextarea}
          data-testid={`${testIdPrefix}-pushback-instructions`}
        />
      </label>
      <ZestButton
        type="button"
        onClick={() => void handlePushBack()}
        disabled={pushing || !target || !instructions.trim()}
        data-testid={`${testIdPrefix}-pushback-btn`}
        zest={{ visualOptions: { variant: "danger", size: "sm" }, busyOptions: { preventRageClick: true } }}
      >
        {pushing ? "Pushing back…" : "Push Back"}
      </ZestButton>
    </div>
  );
}

// ─── signoff button with evidence ──────────────────────────────────────────

interface ISignoffButtonProps {
  release: ReleaseDto;
  signoff: { stageName: string; required: boolean; approved: boolean };
  api: BrokerApi;
  testIdPrefix: string;
  onReleaseUpdated: (release: ReleaseDto) => void;
}

function SignoffButton({ release, signoff, api, testIdPrefix, onReleaseUpdated }: ISignoffButtonProps) {
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const handleApprove = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const updated = await api.signoffReleaseAsync(release.id, signoff.stageName, "user", "Approved via wizard");
      onReleaseUpdated(updated);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, release.id, signoff.stageName, onReleaseUpdated]);

  return (
    <div className={styles.signoffRow}>
      {error && <div className={styles.error}>{error}</div>}
      <span className={styles.signoffStage}>{stageLabel(signoff.stageName)}</span>
      <ZestButton
        type="button"
        onClick={handleApprove}
        disabled={loading}
        data-testid={`${testIdPrefix}-signoff-${signoff.stageName}`}
        zest={{ semanticType: "confirm", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
      >
        {loading ? "Reviewing…" : "Review & Continue"}
      </ZestButton>
    </div>
  );
}

// ─── stage history card ────────────────────────────────────────────────────

interface IStageHistoryCardProps {
  stageRun: ReleaseStageRunDto;
  testIdPrefix: string;
}

function StageHistoryCard({ stageRun, testIdPrefix }: IStageHistoryCardProps) {
  return (
    <div className={styles.stageCard} data-testid={`${testIdPrefix}-stage-${stageRun.stageName}`}>
      <div className={styles.stageHeader}>
        <span className={styles.stageName}>{stageLabel(stageRun.stageName)}</span>
        <span className={`${styles.releaseStatus} ${statusColor(stageRun.status)}`}>{statusLabel(stageRun.status)}</span>
        <span className={styles.phaseBadge}>{phaseLabel(stageRun.phase)}</span>
        {stageRun.questionCount > 0 && (
          <span className={styles.questionBadge}>{stageRun.questionCount} messages</span>
        )}
        <span className={styles.questionBadge}>attempt {stageRun.attempt}</span>
      </div>
      {stageRun.summary && <div className={styles.stageSummary}>{stageRun.summary}</div>}
      {stageRun.gateChecks.length > 0 && (
        <div className={styles.gateChecks}>
          {stageRun.gateChecks.map((gc) => (
            <div key={gc.id} className={styles.gateCheck}>
              <span className={gc.passed ? styles.gatePassed : styles.gateFailed}>
                {gc.passed ? "✓" : "✗"}
              </span>
              <span className={styles.gateName}>{gc.name}</span>
              {gc.evidenceText && (
                <span className={styles.gateEvidence}>{gc.evidenceText}</span>
              )}
            </div>
          ))}
        </div>
      )}
      {stageRun.findings.length > 0 && (
        <div className={styles.findingsSection}>
          <h4>Findings</h4>
          {stageRun.findings.map((f) => (
            <FindingCard key={f.id} finding={f} />
          ))}
        </div>
      )}
    </div>
  );
}

// ─── finding card ──────────────────────────────────────────────────────────

function FindingCard({ finding }: { finding: ReviewFindingDto }) {
  return (
    <div className={styles.findingCard}>
      <span className={`${styles.findingSeverity} ${finding.severity === "Blocker" ? styles.severityBlocker : finding.severity === "Major" ? styles.severityMajor : ""}`}>
        {finding.severity}
      </span>
      <span className={styles.findingTarget}>{finding.target}</span>
      <span className={styles.findingSummary}>{finding.summary}</span>
    </div>
  );
}