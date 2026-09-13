"use client";
import React, { useState } from "react";
import ZestTabs from "jattac.libs.web.zest-tabs";
import BrokerApi from "./Chat/Data/BrokerApi";
import Chat from "./Chat/UI/Chat";
import ReleaseWizard from "./Project/Release/ReleaseWizard";
import styles from "./App.module.css";

type TabValue = "releases" | "chat";

export default function App() {
  const [api] = useState(() => new BrokerApi());
  const [activeTab, setActiveTab] = useState<TabValue>("releases");

  return (
    <div className={styles.app}>
      <header className={styles.tabBar}>
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
        {activeTab === "releases" && <ReleaseWizard api={api} />}
        {activeTab === "chat" && <Chat api={api} />}
      </main>
    </div>
  );
}
