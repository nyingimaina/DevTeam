"use client";
import React, { useCallback, useState } from "react";
import ZestTabs from "jattac.libs.web.zest-tabs";
import BrokerApi from "./Chat/Data/BrokerApi";
import ReleaseWizard from "./Project/Release/ReleaseWizard";
import GitView from "./Project/Git/GitView";
import PathBrowser from "./Project/UI/PathBrowser";
import WorkspaceCleanupNotice from "./UI/WorkspaceCleanupNotice";
import ActiveTurnIndicator from "./UI/ActiveTurnIndicator";
import { formatCleanupNoticeMessage, projectNameFromPath } from "./Project/workspaceCleanup";
import { FaFolderOpen } from "react-icons/fa6";
import styles from "./App.module.css";

type TabValue = "releases" | "git";

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
        <ZestTabs
          id="app-nav"
          items={[
            { label: "Releases", value: "releases" },
            { label: "Git", value: "git" },
          ]}
          activeValue={activeTab}
          onChange={(v) => setActiveTab(v as TabValue)}
        />
      </header>
      <main className={styles.viewPort}>
        <div hidden={activeTab !== "releases"}>
          <ReleaseWizard api={api} workspacePath={project} />
        </div>
        <div hidden={activeTab !== "git"}>
          <GitView api={api} workspacePath={project} />
        </div>
      </main>
      {notice}
      <ActiveTurnIndicator api={api} />
    </div>
  );
}
