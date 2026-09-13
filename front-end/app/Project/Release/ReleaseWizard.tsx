import React, { useCallback, useEffect, useRef, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import {
  ReleaseDto,
  ReviewFindingDto,
  StageRunDto,
} from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
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

function phaseLabel(phase: string): string {
  switch (phase) {
    case "GuidedQA": return "Chat with agent";
    case "Producing": return "Producing artifacts";
    case "Gates": return "Running gates";
    case "Challenge": return "Review in progress";
    case "Signoff": return "Awaiting signoff";
    default: return phase;
  }
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
                <span className={`${styles.releaseStatus} ${statusColor(r.status)}`}>{r.status}</span>
                <span className={styles.releaseStages}>
                  {r.flowPosition?.currentStageName ?? "—"}
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

// ─── release detail with interactive stage ───────────────────────────────

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
  const pendingSignoffs = release.signoffs.filter((s) => s.required && !s.approved);
  const currentStageRun = release.stageRuns
    .filter((sr) => sr.status === "Active" || sr.status === "BlockedGate" || sr.status === "BlockedSignoff")
    .sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime())[0];

  return (
    <div className={styles.detailView}>
      <div className={styles.header}>
        <ZestButton type="button" onClick={onBack} disabled={loading}
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}>Back</ZestButton>
        <h2>{release.title ?? release.features[0]?.key}</h2>
        <span className={`${styles.releaseStatus} ${statusColor(release.status)}`}>{release.status}</span>
        <ZestButton type="button" onClick={onRefresh} disabled={loading}
          zest={{ semanticType: "refresh", busyOptions: { preventRageClick: true }, buttonStyle: "text", visualOptions: { size: "sm" } }}>
          Refresh
        </ZestButton>
      </div>

      <div className={styles.detailInfo}>
        <span>ID: {release.id.slice(0, 8)}...</span>
        <span>Workspace: {release.workspacePath}</span>
        <span>Stage: {release.flowPosition?.currentStageName ?? "—"}</span>
      </div>

      {/* Interactive stage panel */}
      <StagePanel
        release={release}
        api={api}
        testIdPrefix={testIdPrefix}
        loading={loading}
        onReleaseUpdated={onReleaseUpdated}
      />

      {/* Signoff buttons */}
      {pendingSignoffs.length > 0 && (
        <div className={styles.signoffSection}>
          <h3>Signoff Required</h3>
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

      {/* Stage history */}
      {release.stageRuns.length > 0 && (
        <div className={styles.stagesSection}>
          <h3>Stage History</h3>
          {release.stageRuns.map((sr) => (
            <StageHistoryCard key={sr.id} stageRun={sr} testIdPrefix={testIdPrefix} />
          ))}
        </div>
      )}
    </div>
  );
}

// ─── stage panel (start, chat, run gates) ────────────────────────────────

interface IStagePanelProps {
  release: ReleaseDto;
  api: BrokerApi;
  testIdPrefix: string;
  loading: boolean;
  onReleaseUpdated: (release: ReleaseDto) => void;
}

function StagePanel({ release, api, testIdPrefix, loading, onReleaseUpdated }: IStagePanelProps) {
  const [activeStage, setActiveStage] = useState<StageRunDto | null>(null);
  const [messages, setMessages] = useState<{ role: "user" | "agent"; text: string }[]>([]);
  const [input, setInput] = useState("");
  const [stageLoading, setStageLoading] = useState(false);
  const [stageError, setStageError] = useState<string | null>(null);
  const messagesEndRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  const handleStartStage = useCallback(async () => {
    setStageLoading(true);
    setStageError(null);
    try {
      const stageRun = await api.startStageAsync(release.id);
      setActiveStage(stageRun);
      setMessages([{ role: "agent", text: "Stage started. Ask me anything about this phase." }]);
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setStageLoading(false);
    }
  }, [api, release.id]);

  const handleSendMessage = useCallback(async () => {
    if (!input.trim() || !activeStage) return;
    const userMsg = input.trim();
    setInput("");
    setMessages((prev) => [...prev, { role: "user", text: userMsg }]);
    setStageLoading(true);
    setStageError(null);
    try {
      await api.sendStageMessageAsync(release.id, userMsg);
      setMessages((prev) => [...prev, { role: "agent", text: "Message sent. Continue the conversation or run gates when ready." }]);
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setStageLoading(false);
    }
  }, [api, release.id, input, activeStage]);

  const handleRunGates = useCallback(async () => {
    setStageLoading(true);
    setStageError(null);
    try {
      const updated = await api.runStageGatesAsync(release.id);
      onReleaseUpdated(updated);
      setMessages((prev) => [...prev, { role: "agent", text: "Gates completed. Check results below." }]);
      setActiveStage(null);
    } catch (e) {
      setStageError(toErrorMessage(e));
    } finally {
      setStageLoading(false);
    }
  }, [api, release.id, onReleaseUpdated]);

  // If no active stage and no stage run in progress, show start button
  if (!activeStage && !currentStageRun(release)) {
    return (
      <div className={styles.stagePanel}>
        <div className={styles.stageHeader}>
          <h3>Current Stage: {release.flowPosition?.currentStageName ?? "—"}</h3>
          <ZestButton
            type="button"
            onClick={handleStartStage}
            disabled={stageLoading || release.status === "Ready"}
            data-testid={`${testIdPrefix}-start-stage-btn`}
            zest={{ semanticType: "submit", busyOptions: { preventRageClick: true } }}
          >
            {stageLoading ? "Starting..." : "Start Stage"}
          </ZestButton>
        </div>
      </div>
    );
  }

  // Show chat panel
  return (
    <div className={styles.stagePanel}>
      {stageError && <div className={styles.error}>{stageError}</div>}

      <div className={styles.chatContainer}>
        {messages.map((msg, i) => (
          <div key={i} className={`${styles.chatMessage} ${msg.role === "user" ? styles.chatUser : styles.chatAgent}`}>
            <div className={styles.chatRole}>{msg.role === "user" ? "You" : "Agent"}</div>
            <div className={styles.chatText}>{msg.text}</div>
          </div>
        ))}
        <div ref={messagesEndRef} />
      </div>

      <div className={styles.chatInput}>
        <input
          value={input}
          onChange={(e) => setInput(e.target.value)}
          placeholder="Type your message..."
          className={styles.input}
          onKeyDown={(e) => e.key === "Enter" && handleSendMessage()}
          disabled={stageLoading}
          data-testid={`${testIdPrefix}-chat-input`}
        />
        <ZestButton
          type="button"
          onClick={handleSendMessage}
          disabled={stageLoading || !input.trim()}
          data-testid={`${testIdPrefix}-send-btn`}
          zest={{ semanticType: "submit", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
        >
          Send
        </ZestButton>
        <ZestButton
          type="button"
          onClick={handleRunGates}
          disabled={stageLoading}
          data-testid={`${testIdPrefix}-run-gates-btn`}
          zest={{ semanticType: "confirm", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
        >
          Run Gates
        </ZestButton>
      </div>
    </div>
  );
}

function currentStageRun(release: ReleaseDto): StageRunDto | undefined {
  return release.stageRuns
    .filter((sr) => sr.status === "Active" || sr.status === "BlockedGate" || sr.status === "BlockedSignoff")
    .sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime())[0];
}

// ─── signoff button with evidence ────────────────────────────────────────

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
      <span className={styles.signoffStage}>{signoff.stageName}</span>
      <ZestButton
        type="button"
        onClick={handleApprove}
        disabled={loading}
        data-testid={`${testIdPrefix}-signoff-${signoff.stageName}`}
        zest={{ semanticType: "confirm", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
      >
        {loading ? "Approving..." : `Approve ${signoff.stageName}`}
      </ZestButton>
    </div>
  );
}

// ─── stage history card ──────────────────────────────────────────────────

interface IStageHistoryCardProps {
  stageRun: StageRunDto;
  testIdPrefix: string;
}

function StageHistoryCard({ stageRun, testIdPrefix }: IStageHistoryCardProps) {
  return (
    <div className={styles.stageCard} data-testid={`${testIdPrefix}-stage-${stageRun.stageName}`}>
      <div className={styles.stageHeader}>
        <span className={styles.stageName}>{stageRun.stageName}</span>
        <span className={`${styles.releaseStatus} ${statusColor(stageRun.status)}`}>{stageRun.status}</span>
        <span className={styles.phaseBadge}>{phaseLabel(stageRun.phase)}</span>
        {stageRun.questionCount > 0 && (
          <span className={styles.questionBadge}>{stageRun.questionCount} messages</span>
        )}
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

// ─── finding card ────────────────────────────────────────────────────────

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
