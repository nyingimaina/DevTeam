"use client";
import React, { useState } from "react";
import ZestTabs from "jattac.libs.web.zest-tabs";
import BrokerApi from "../../Chat/Data/BrokerApi";
import ProfilesView from "./ProfilesView";
import PipelineView from "./PipelineView";
import SpecialistsView from "./SpecialistsView";
import SupportView from "./SupportView";
import ModelsView from "./ModelsView";
import styles from "../Styles/SettingsView.module.css";

type SettingsTabValue = "profiles" | "pipeline" | "specialists" | "models" | "support";

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
        items={[
          { label: "Profiles", value: "profiles" },
          { label: "Pipeline", value: "pipeline" },
          { label: "Specialists", value: "specialists" },
          { label: "Models", value: "models" },
          { label: "Support", value: "support" },
        ]}
        activeValue={activeTab}
        onChange={(v) => setActiveTab(v as SettingsTabValue)}
      />
      <div hidden={activeTab !== "profiles"}>
        <ProfilesView api={api} workspacePath={workspacePath} />
      </div>
      <div hidden={activeTab !== "pipeline"}>
        <PipelineView api={api} workspacePath={workspacePath} />
      </div>
      <div hidden={activeTab !== "specialists"}>
        <SpecialistsView api={api} />
      </div>
      <div hidden={activeTab !== "models"} data-testid="settings-models-tab-panel">
        <ModelsView api={api} workspacePath={workspacePath} />
      </div>
      <div hidden={activeTab !== "support"} data-testid="settings-support-tab-panel">
        <SupportView api={api} />
      </div>
    </div>
  );
}
