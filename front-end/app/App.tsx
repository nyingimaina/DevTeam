"use client";
import React, { useCallback, useEffect, useState } from "react";
import SidekickMenu, { ISidekickMenuItem } from "jattac.libs.web.zest-sidekick-menu";
import BrokerApi from "./Chat/Data/BrokerApi";
import ProjectNavigator from "./Project/Navigation/ProjectNavigator";
import GitView from "./Project/Git/GitView";
import ChecksView from "./Project/Checks/ChecksView";
import InsightsView from "./Project/Insights/InsightsView";
import SettingsView from "./Project/Settings/SettingsView";
import PathBrowser from "./Project/UI/PathBrowser";
import WorkspaceCleanupNotice from "./UI/WorkspaceCleanupNotice";
import ActiveTurnIndicator from "./UI/ActiveTurnIndicator";
import ContextDial from "./UI/ContextDial";
import { formatCleanupNoticeMessage, projectNameFromPath } from "./Project/workspaceCleanup";
import { installGlobalErrorHandlers } from "./UI/diagnostics";
import { FaFolderOpen, FaListCheck, FaCodeBranch, FaShieldHalved, FaGear, FaChartLine } from "react-icons/fa6";
import styles from "./App.module.css";

type TabValue = "releases" | "checks" | "insights" | "git" | "settings";

const STORAGE_KEY = "devteam-project";

function readProject(): string | null {
  if (typeof window === "undefined") return null;
  try {
    return localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

export default function App() {
  const [api] = useState(() => new BrokerApi());
  const [project, setProject] = useState<string | null>(readProject);
  const [activeTab, setActiveTab] = useState<TabValue>("releases");
  const [cleanupNotice, setCleanupNotice] = useState<string | null>(null);

  // Record anything the UI itself trips over, so "it just broke" becomes something the Support
  // tab can hand to a specialist. Idempotent.
  useEffect(() => {
    installGlobalErrorHandlers();
  }, []);

  const runWorkspaceCleanupAsync = useCallback(async (path: string) => {
    try {
      const stopped = await api.cleanupWorkspaceAsync(path);
      const message = formatCleanupNoticeMessage(path, stopped);
      if (message) setCleanupNotice(message);
    } catch {
      // A failed sweep should never block opening or closing a project.
    }
  }, [api]);

  const openFolder = useCallback((path: string) => {
    setProject(path);
    try {
      localStorage.setItem(STORAGE_KEY, path);
    } catch { /* ignore */ }
    void runWorkspaceCleanupAsync(path);
  }, [runWorkspaceCleanupAsync]);

  const closeProject = useCallback(() => {
    if (project) void runWorkspaceCleanupAsync(project);
    setProject(null);
    try {
      localStorage.removeItem(STORAGE_KEY);
    } catch { /* ignore */ }
  }, [project, runWorkspaceCleanupAsync]);

  const notice = cleanupNotice && (
    <WorkspaceCleanupNotice message={cleanupNotice} onDismiss={() => setCleanupNotice(null)} />
  );

  if (!project) {
    return (
      <div className={styles.openFolder}>
        <div className={styles.openFolderIcon}>
          <FaFolderOpen size={40} />
        </div>
        <div className={styles.openFolderText}>
          Open a folder to start a project.
        </div>
        <PathBrowser api={api} mode="pickDirectory" onSelect={openFolder} />
        {notice}
        <ActiveTurnIndicator api={api} />
      </div>
    );
  }

  const projectName = projectNameFromPath(project);

  const navItems: ISidekickMenuItem[] = [
    {
      id: "releases",
      label: "Releases",
      icon: <FaListCheck />,
      searchTerms: "releases workflow pipeline stages",
      onClick: () => setActiveTab("releases"),
    },
    {
      id: "checks",
      label: "Checks",
      icon: <FaShieldHalved />,
      searchTerms: "checks quality tests build lint coverage",
      onClick: () => setActiveTab("checks"),
    },
    {
      id: "insights",
      label: "Efficiency",
      icon: <FaChartLine />,
      searchTerms: "efficiency insights tokens cost time metrics performance",
      onClick: () => setActiveTab("insights"),
    },
    {
      id: "git",
      label: "Git",
      icon: <FaCodeBranch />,
      searchTerms: "git source control branches commits",
      onClick: () => setActiveTab("git"),
    },
    {
      id: "settings",
      label: "Settings",
      icon: <FaGear />,
      searchTerms: "settings profiles preferences",
      onClick: () => setActiveTab("settings"),
    },
  ];

  return (
    <div className={styles.app}>
      <header className={styles.tabBar}>
        <button
          type="button"
          className={styles.projectName}
          onClick={closeProject}
          title={`Switch project (current: ${project})`}
        >
          {projectName}
        </button>
        {/* In the tab bar rather than floating over the page: the bar is already sticky, and as a
            flex sibling ahead of the menu the dial can never end up sitting on top of the sidekick
            hamburger the way an absolutely-positioned overlay would. */}
        <ContextDial api={api} />
        <SidekickMenu
          items={navItems}
          side="right"
          openOnDesktop={false}
          headerContent={
            <div className={styles.brand}>
              <img src="/devteam-icon.svg" alt="" width={32} height={32} />
              <span className={styles.brandName}>DevTeam</span>
            </div>
          }
        />
      </header>
      <main className={styles.viewPort}>
        <div hidden={activeTab !== "releases"}>
          <ProjectNavigator api={api} workspacePath={project} active={activeTab === "releases"} />
        </div>
        <div hidden={activeTab !== "checks"} data-testid="checks-tab-panel">
          <ChecksView api={api} workspacePath={project} />
        </div>
        <div hidden={activeTab !== "insights"} data-testid="insights-tab-panel">
          <InsightsView api={api} workspacePath={project} />
        </div>
        <div hidden={activeTab !== "git"}>
          <GitView api={api} workspacePath={project} />
        </div>
        <div hidden={activeTab !== "settings"} data-testid="settings-tab-panel">
          <SettingsView api={api} workspacePath={project} />
        </div>
      </main>
      {notice}
      <ActiveTurnIndicator api={api} />
    </div>
  );
}
