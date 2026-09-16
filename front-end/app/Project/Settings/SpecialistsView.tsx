"use client";
import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { SpecialistRoleDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import ZestTextbox from "jattac.libs.web.zest-textbox";
import ResponsiveTable, { ColumnDefinition } from "jattac.libs.web.responsive-table";
import styles from "../Styles/ProfilesView.module.css";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

interface ISpecialistsViewProps {
  api: BrokerApi;
  testIdPrefix?: string;
}

export default function SpecialistsView({ api, testIdPrefix = "specialist" }: ISpecialistsViewProps) {
  const [specialists, setSpecialists] = useState<SpecialistRoleDto[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [editName, setEditName] = useState("");
  const [editDescription, setEditDescription] = useState("");
  const [editPrompt, setEditPrompt] = useState("");
  const [editWritesCode, setEditWritesCode] = useState(false);
  const [newName, setNewName] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const loadAll = useCallback(async () => {
    try {
      setSpecialists(await api.getSpecialistsAsync());
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api]);

  useEffect(() => { void loadAll(); }, [loadAll]);

  const selected = specialists.find((s) => s.id === selectedId) ?? null;

  const openEditor = useCallback((specialist: SpecialistRoleDto) => {
    setSelectedId(specialist.id);
    setEditName(specialist.name);
    setEditDescription(specialist.description);
    setEditPrompt(specialist.primingPrompt);
    setEditWritesCode(specialist.writesCode);
  }, []);

  const handleSave = useCallback(async () => {
    if (!selected) return;
    setLoading(true);
    setError(null);
    try {
      const updated = await api.updateSpecialistAsync(selected.id, editName, editDescription, editPrompt, editWritesCode);
      setSpecialists((prev) => prev.map((s) => (s.id === updated.id ? updated : s)));
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, selected, editName, editDescription, editPrompt, editWritesCode]);

  const handleCreate = useCallback(async () => {
    if (!newName.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const created = await api.createSpecialistAsync(newName.trim(), "", "", false);
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
      await api.deleteSpecialistAsync(id);
      if (selectedId === id) setSelectedId(null);
      await loadAll();
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api, selectedId, loadAll]);

  const columns: ColumnDefinition<SpecialistRoleDto>[] = [
    { displayLabel: "Name", cellRenderer: (row) => row.name },
    { displayLabel: "Description", cellRenderer: (row) => row.description },
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
  ];

  return (
    <div className={styles.container} data-testid={testIdPrefix}>
      {error && <div className={styles.error}>{error}</div>}
      <p>Specialists any stage can consult on demand — see the Pipeline tab for how a stage&apos;s own gates work.</p>

      <ResponsiveTable<SpecialistRoleDto>
        columnDefinitions={columns}
        data={specialists}
        onRowClick={openEditor}
        noDataComponent={<div>No specialists yet — add one below.</div>}
      />

      <div className={styles.newProfile}>
        <ZestTextbox
          data-testid="new-specialist-name-input"
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          placeholder="New specialist name (e.g. database-admin)"
        />
        <ZestButton
          type="button"
          data-testid="new-specialist-create-btn"
          onClick={() => void handleCreate()}
          disabled={!newName.trim() || loading}
          zest={{ semanticType: "add", busyOptions: { preventRageClick: true } }}
        >
          Add Specialist
        </ZestButton>
      </div>

      {selected && (
        <div className={styles.editor} data-testid="specialist-editor">
          <label>
            Name
            <ZestTextbox
              data-testid="specialist-name-input"
              value={editName}
              onChange={(e) => setEditName(e.target.value)}
            />
          </label>
          <label>
            Description (shown to an agent deciding whether this specialist can help)
            <ZestTextbox
              data-testid="specialist-description-input"
              value={editDescription}
              onChange={(e) => setEditDescription(e.target.value)}
            />
          </label>
          <label>
            Priming prompt
            <ZestTextbox
              data-testid="specialist-prompt-textarea"
              value={editPrompt}
              onChange={(e) => setEditPrompt(e.target.value)}
              placeholder="You are a database administrator who..."
              zest={{ isMultiline: true }}
            />
          </label>
          <label className={styles.overrideLabel}>
            <input
              type="checkbox"
              data-testid="specialist-writescode-checkbox"
              checked={editWritesCode}
              onChange={(e) => setEditWritesCode(e.target.checked)}
            />
            Writes code (gets the feature&apos;s code paths, not just its docs)
          </label>
          <ZestButton
            type="button"
            data-testid="specialist-save-btn"
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
