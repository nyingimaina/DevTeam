"use client";
import React, { useState } from "react";
import ZestTabs from "jattac.libs.web.zest-tabs";
import BrokerApi from "../../Chat/Data/BrokerApi";
import ProfilesView from "./ProfilesView";
import styles from "../Styles/SettingsView.module.css";

type SettingsTabValue = "profiles";

interface ISettingsViewProps {
  api: BrokerApi;
  workspacePath: string;
}

export default function SettingsView({ api, workspacePath }: ISettingsViewProps) {
  const [activeTab, setActiveTab] = useState<SettingsTabValue>("profiles");

  return (
    <div className={styles.container} data-testid="settings-view">
      <ZestTabs
        id="settings-nav"
        items={[{ label: "Profiles", value: "profiles" }]}
        activeValue={activeTab}
        onChange={(v) => setActiveTab(v as SettingsTabValue)}
      />
      <div hidden={activeTab !== "profiles"}>
        <ProfilesView api={api} workspacePath={workspacePath} />
      </div>
    </div>
  );
}
