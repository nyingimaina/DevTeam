"use client";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ProfileDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import ZestTextbox from "jattac.libs.web.zest-textbox";
import ResponsiveTable, { ColumnDefinition } from "jattac.libs.web.responsive-table";
import SelectWrapper from "../../Forms/SelectWrapper/UI/SelectWrapper";
import styles from "../Styles/ProfilesView.module.css";

const STAGE_NAMES = ["business-analyst", "developer", "qa"];

// react-select (via SelectWrapper) inspects each item with the `in` operator to detect
// option groups, which throws on primitive strings — options must be objects.
interface IStageOption {
  name: string;
}
const STAGE_OPTIONS: IStageOption[] = STAGE_NAMES.map((name) => ({ name }));

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

interface IProfilesViewProps {
  api: BrokerApi;
  workspacePath: string;
  testIdPrefix?: string;
}

export default function ProfilesView({ api, workspacePath, testIdPrefix = "profile" }: IProfilesViewProps) {
  const [profiles, setProfiles] = useState<ProfileDto[]>([]);
  const [workspaceProfileId, setWorkspaceProfileId] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [activeStage, setActiveStage] = useState(STAGE_NAMES[0]);
  const [editName, setEditName] = useState("");
  const [editDescription, setEditDescription] = useState("");
  const [editPrompts, setEditPrompts] = useState<Record<string, string>>({});
  const [editOverrides, setEditOverrides] = useState<Record<string, boolean>>({});
  const [newName, setNewName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const loadAll = useCallback(async () => {
    try {
      const [profileList, workspaceProfile] = await Promise.all([
        api.getProfilesAsync(),
        api.getWorkspaceProfileAsync(workspacePath),
      ]);
      setProfiles(profileList);
      setWorkspaceProfileId(workspaceProfile.profileId);
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api, workspacePath]);

  useEffect(() => { void loadAll(); }, [loadAll]);

  const selected = profiles.find((p) => p.id === selectedId) ?? null;

  const openEditor = useCallback((profile: ProfileDto) => {
    setSelectedId(profile.id);
    setEditName(profile.name);
    setEditDescription(profile.description ?? "");
    const prompts: Record<string, string> = {};
    const overrides: Record<string, boolean> = {};
    for (const stageName of STAGE_NAMES) {
      const stagePrompt = profile.prompts.find((p) => p.stageName === stageName);
      prompts[stageName] = stagePrompt?.promptText ?? "";
      overrides[stageName] = stagePrompt?.overridesBuiltInPrompt ?? false;
    }
    setEditPrompts(prompts);
    setEditOverrides(overrides);
    setActiveStage(STAGE_NAMES[0]);
  }, []);

  const handleSave = useCallback(async () => {
    if (!selected) return;
    setLoading(true);
    setError(null);
    try {
      const prompts = STAGE_NAMES.map((stageName) => ({
        stageName,
        promptText: editPrompts[stageName] ?? "",
        overridesBuiltInPrompt: editOverrides[stageName] ?? false,
      }));
      const updated = await api.updateProfileAsync(selected.id, editName, editDescription || null, prompts);
      setProfiles((prev) => prev.map((p) => (p.id === updated.id ? updated : p)));
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, selected, editName, editDescription, editPrompts, editOverrides]);

  const handleCreate = useCallback(async () => {
    if (!newName.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const created = await api.createProfileAsync(newName.trim(), null);
      setNewName("");
      await loadAll();
      openEditor(created);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, newName, loadAll, openEditor]);

  const handleDelete = useCallback(async (id: string) => {
    setError(null);
    try {
      await api.deleteProfileAsync(id);
      if (selectedId === id) setSelectedId(null);
      await loadAll();
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api, selectedId, loadAll]);

  const handleSetDefault = useCallback(async (id: string) => {
    setError(null);
    try {
      await api.setDefaultProfileAsync(id);
      await loadAll();
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api, loadAll]);

  const handleWorkspaceProfileChange = useCallback(async (id: string) => {
    setWorkspaceProfileId(id);
    setError(null);
    try {
      await api.setWorkspaceProfileAsync(workspacePath, id);
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api, workspacePath]);

  const columns = useMemo<ColumnDefinition<ProfileDto>[]>(() => [
    { displayLabel: "Name", cellRenderer: (row) => row.name },
    { displayLabel: "Description", cellRenderer: (row) => row.description },
    {
      displayLabel: "Default",
      cellRenderer: (row) => row.isDefault ? (
        <span>✓ Default</span>
      ) : (
        <ZestButton
          type="button"
          data-rt-ignore-row-click
          data-testid={`${testIdPrefix}-set-default-${row.id}`}
          onClick={() => void handleSetDefault(row.id)}
          zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
        >
          Set Default
        </ZestButton>
      ),
    },
    {
      displayLabel: "",
      cellRenderer: (row) => (
        <ZestButton
          type="button"
          data-rt-ignore-row-click
          data-testid={`${testIdPrefix}-delete-${row.id}`}
          onClick={() => void handleDelete(row.id)}
          zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
        >
          Delete
        </ZestButton>
      ),
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [testIdPrefix, handleSetDefault, handleDelete]);

  return (
    <div className={styles.container} data-testid={testIdPrefix}>
      {error && <div className={styles.error}>{error}</div>}

      <div className={styles.workspacePicker}>
        <label>
          Active profile for this workspace
          <div data-testid="workspace-profile-picker">
            <SelectWrapper<ProfileDto>
              data={profiles}
              selectedResolver={(p) => p.id === workspaceProfileId}
              valueResolver={(p) => p.id}
              labelResolver={(p) => p.name}
              onChange={(items) => {
                if (items[0]) void handleWorkspaceProfileChange(items[0].id);
              }}
            />
          </div>
        </label>
      </div>

      <ResponsiveTable<ProfileDto>
        columnDefinitions={columns}
        data={profiles}
        onRowClick={openEditor}
        noDataComponent={<div>No profiles yet — create one below.</div>}
      />

      <div className={styles.newProfile}>
        <ZestTextbox
          data-testid="new-profile-name-input"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          placeholder="New profile name"
        />
        <ZestButton
          type="button"
          data-testid="new-profile-create-btn"
          onClick={() => void handleCreate()}
          disabled={!newName.trim() || loading}
          zest={{ semanticType: "add", busyOptions: { preventRageClick: true } }}
        >
          Create Profile
        </ZestButton>
      </div>

      {selected && (
        <div className={styles.editor} data-testid="profile-editor">
          <label>
            Name
            <ZestTextbox
              data-testid="profile-name-input"
              value={editName}
              onChange={(e) => setEditName(e.target.value)}
            />
          </label>
          <label>
            Description
            <ZestTextbox
              data-testid="profile-description-input"
              value={editDescription}
              onChange={(e) => setEditDescription(e.target.value)}
            />
          </label>
          <label>
            Stage
            <div data-testid="profile-stage-picker">
              <SelectWrapper<IStageOption>
                data={STAGE_OPTIONS}
                selectedResolver={(s) => s.name === activeStage}
                valueResolver={(s) => s.name}
                labelResolver={(s) => s.name}
                onChange={(items) => {
                  if (items[0]) setActiveStage(items[0].name);
                }}
              />
            </div>
          </label>
          <ZestTextbox
            data-testid="profile-prompt-textarea"
            value={editPrompts[activeStage] ?? ""}
            onChange={(e) => setEditPrompts((prev) => ({ ...prev, [activeStage]: e.target.value }))}
            placeholder="Additional guidance sent to the agent for this stage…"
            zest={{ isMultiline: true }}
          />
          <label className={styles.overrideLabel}>
            <input
              type="checkbox"
              data-testid="profile-override-checkbox"
              checked={editOverrides[activeStage] ?? false}
              onChange={(e) => setEditOverrides((prev) => ({ ...prev, [activeStage]: e.target.checked }))}
            />
            Override the built-in prompt for this stage
          </label>
          {editOverrides[activeStage] && (
            <div className={styles.overrideWarning}>
              This replaces the entire built-in prompt for this stage, including handoff and artifact
              instructions. Only the text above is sent to the agent.
            </div>
          )}
          <ZestButton
            type="button"
            data-testid="profile-save-btn"
            onClick={() => void handleSave()}
            disabled={loading}
            zest={{ semanticType: "save", busyOptions: { preventRageClick: true } }}
          >
            Save
          </ZestButton>
        </div>
      )}
    </div>
  );
}
