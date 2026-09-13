"use client";
import React, { useCallback, useEffect, useReducer, useState } from "react";
import ChatLogic from "../State/ChatLogic";
import MessageRow from "./MessageRow";
import LiveAssistantBubble from "./LiveAssistantBubble";
import AutoGrowTextarea from "./AutoGrowTextarea";
import ModelSidePane from "./ModelSidePane";
import { useTheme } from "../../Theme/ThemeProvider";
import { SidePaneProvider, useSidePane, ZestResponsiveLayout } from "jattac.libs.web.zest-responsive-layout";
import ZestButton from "jattac.libs.web.zest-button";
import ZestTextbox from "jattac.libs.web.zest-textbox";
import { FaRobot, FaList } from "react-icons/fa6";
import styles from "../Styles/Chat.module.css";

export default function Chat() {
  return (
    <SidePaneProvider>
      <ZestResponsiveLayout>
        <ChatInner />
      </ZestResponsiveLayout>
    </SidePaneProvider>
  );
}

function ChatInner() {
  const [logic] = useState(() => new ChatLogic());
  const [, forceRender] = useReducer((x: number) => x + 1, 0);
  const [draft, setDraft] = useState("");
  const { openSidePane, closeSidePane } = useSidePane();
  const { mode, setMode } = useTheme();

  useEffect(() => {
    logic.setRerender(() => forceRender());
    void logic.initializeAsync();
    return () => {
      void logic.disposeAsync();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const r = logic.repository;

  const send = useCallback(() => {
    const text = draft.trim();
    if (!text || r.isPending) return;
    setDraft("");
    void logic.sendPromptAsync(text);
  }, [draft, r.isPending, logic]);

  const showWorkspacePicker = useCallback(() => {
    void openSidePane<{ workspacePath: string } | null>({
      title: "New workspace",
      content: (
        <WorkspacePicker
          onPick={async (path) => {
            await logic.createSessionAsync(path);
            closeSidePane(null);
          }}
        />
      ),
    });
  }, [logic, openSidePane, closeSidePane]);

  const showSidePane = useCallback(() => {
    void openSidePane<string | null>({
      title: "DevTeam",
      content: (
        <ModelSidePane
          models={r.activeSession?.models ?? []}
          currentModelId={r.currentModelId}
          modes={r.activeSession?.modes ?? []}
          currentModeId={r.currentModeId}
          sessions={r.sessions}
          activeSessionId={r.activeSession?.sessionId}
          themeMode={mode}
          onThemeModeChange={setMode}
          onSelectModel={async (modelId) => {
            await logic.switchModelAsync(modelId);
            closeSidePane(null);
          }}
          onSelectMode={(modeId) => void logic.switchModeAsync(modeId)}
          onSelectSession={async (sessionId) => {
            await logic.switchSessionAsync(sessionId);
            closeSidePane(null);
          }}
          onNewSession={() => {
            closeSidePane(null);
            showWorkspacePicker();
          }}
          onDeleteSession={async (sessionId) => {
            await logic.deleteSessionAsync(sessionId);
          }}
          onClose={() => closeSidePane(null)}
          busy={r.busy || r.isPending}
        />
      ),
    });
  }, [r, logic, openSidePane, closeSidePane, showWorkspacePicker, mode, setMode]);

  if (!r.brokerReady) {
    return (
      <div className={styles.centerScreen}>
        <div className={styles.logoIcon}>
          <FaRobot size={40} />
        </div>
        <div className={styles.errorText}>{r.error ?? "Connecting to the broker…"}</div>
        <ZestButton
          type="button"
          onClick={() => void logic.initializeAsync()}
          zest={{ visualOptions: { variant: "standard" } }}
        >
          Retry
        </ZestButton>
      </div>
    );
  }

  if (!r.activeSession) {
    return (
      <div className={styles.centerScreen}>
        <div className={styles.logoIcon}>
          <FaRobot size={40} />
        </div>
        <div className={styles.pickerIntro}>
          DevTeam is a chat surface into your opencode-powered agent. Pick a workspace folder to
          start.
        </div>
        <WorkspacePicker onPick={(path) => void logic.createSessionAsync(path)} />
        <div className={styles.pickerHint}>
          {r.agentName ? `Connected to ${r.agentName}.` : "Checking broker…"}
          {r.brokerVersion ? ` Broker v${r.brokerVersion}.` : ""}
        </div>
      </div>
    );
  }

  return (
    <div className={styles.chatRoot}>
      <div className={styles.threadScroller}>
        <div className={styles.thread}>
          {r.activeSession.messages.length === 0 && (
            <div className={styles.threadIntro}>
              Chatting in{" "}
              <span className={styles.workspacePath}>{r.activeSession.workspacePath}</span>
              <div className={styles.threadSub}>
                Ask it to plan, scaffold, fix, test — it works in the workspace as your agent.
              </div>
            </div>
          )}
          {r.activeSession.messages.map((message) => (
            <MessageRow key={message.id} message={message} />
          ))}
          {r.liveAssistant && <LiveAssistantBubble live={r.liveAssistant} />}
        </div>
      </div>

      <div className={styles.dock}>
        <button
          type="button"
          className={styles.menuButton}
          onClick={showSidePane}
          aria-label="Open side pane"
        >
          <FaList size={18} />
        </button>
        <AutoGrowTextarea
          value={draft}
          onChange={setDraft}
          onEnter={send}
          placeholder="Ask DevTeam…  (Enter to send, Shift+Enter for a new line)"
          disabled={r.isPending}
        />
        <ZestButton
          type="button"
          onClick={send}
          disabled={r.isPending || draft.trim().length === 0}
          zest={{ visualOptions: { variant: "standard" } }}
        >
          Send
        </ZestButton>
      </div>
      {r.error && <div className={styles.errorBanner}>{r.error}</div>}
    </div>
  );
}

interface IWorkspacePickerProps {
  onPick: (workspacePath: string) => void;
}

function WorkspacePicker({ onPick }: IWorkspacePickerProps) {
  const [path, setPath] = useState("");
  return (
    <div className={styles.picker}>
      <ZestTextbox
        className={styles.pathInput}
        value={path}
        onChange={(e) => setPath(e.target.value)}
        placeholder="C:\work\my-project"
        maxLength={260}
        autoFocus
      />
<ZestButton
          type="button"
          onClick={() => onPick(path.trim())}
          disabled={path.trim().length === 0}
          zest={{ visualOptions: { variant: "standard" } }}
        >
        Start workspace
      </ZestButton>
    </div>
  );
}