"use client";
import React, { useCallback, useState } from "react";
import ZestTabs from "jattac.libs.web.zest-tabs";
import BrokerApi from "./Chat/Data/BrokerApi";
import Chat from "./Chat/UI/Chat";
import ReleaseWizard from "./Project/Release/ReleaseWizard";
import PathBrowser from "./Project/UI/PathBrowser";
import { FaFolderOpen } from "react-icons/fa6";
import styles from "./App.module.css";

type TabValue = "releases" | "chat";

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

  const openFolder = useCallback((path: string) => {
    setProject(path);
    try {
      localStorage.setItem(STORAGE_KEY, path);
    } catch { /* ignore */ }
  }, []);

  const closeProject = useCallback(() => {
    setProject(null);
    try {
      localStorage.removeItem(STORAGE_KEY);
    } catch { /* ignore */ }
  }, []);

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
      </div>
    );
  }

  const projectName = project.split(/[\\/]/).pop() ?? project;

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
            { label: "Chat", value: "chat" },
          ]}
          activeValue={activeTab}
          onChange={(v) => setActiveTab(v as TabValue)}
        />
      </header>
      <main className={styles.viewPort}>
        {activeTab === "releases" && <ReleaseWizard api={api} workspacePath={project} />}
        {activeTab === "chat" && <Chat api={api} workspacePath={project} />}
      </main>
    </div>
  );
}
