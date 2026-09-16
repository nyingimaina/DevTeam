"use client";
import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { GateStepEditorDto, PipelineEditorRoleDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import ZestTextbox from "jattac.libs.web.zest-textbox";
import { stageLabel } from "../Release/labels";
import styles from "../Styles/PipelineView.module.css";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function emptyRole(name: string): PipelineEditorRoleDto {
  return { name, writesCode: false, signoff: null, userInputRequired: false, stepSummary: [], entryGates: [], exitGatePrompts: [] };
}

interface IPipelineViewProps {
  api: BrokerApi;
  workspacePath: string;
  testIdPrefix?: string;
}

export default function PipelineView({ api, workspacePath, testIdPrefix = "pipeline" }: IPipelineViewProps) {
  const [roles, setRoles] = useState<PipelineEditorRoleDto[]>([]);
  const [newName, setNewName] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const data = await api.getWorkspacePipelineAsync(workspacePath);
      setRoles(data.roles);
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api, workspacePath]);

  useEffect(() => { void load(); }, [load]);

  const updateRole = useCallback((index: number, patch: Partial<PipelineEditorRoleDto>) => {
    setRoles((prev) => prev.map((r, i) => (i === index ? { ...r, ...patch } : r)));
  }, []);

  const moveRole = useCallback((index: number, direction: -1 | 1) => {
    setRoles((prev) => {
      const target = index + direction;
      if (target < 0 || target >= prev.length) return prev;
      const next = [...prev];
      [next[index], next[target]] = [next[target], next[index]];
      return next;
    });
  }, []);

  const removeRole = useCallback((index: number) => {
    setRoles((prev) => prev.filter((_, i) => i !== index));
  }, []);

  const addRole = useCallback(() => {
    const name = newName.trim();
    if (!name) return;
    setRoles((prev) => [...prev, emptyRole(name)]);
    setNewName("");
  }, [newName]);

  const handleSave = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const saved = await api.saveWorkspacePipelineAsync(workspacePath, roles);
      setRoles(saved.roles);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath, roles]);

  return (
    <div className={styles.container} data-testid={testIdPrefix}>
      {error && <div className={styles.error}>{error}</div>}
      <p className={styles.hint}>
        Reorder, add, or remove stages, and attach entry/exit gate checks. A stage&apos;s own
        agent and builtin-gate wiring stays hand-authored in devteam/release.yaml — this
        editor never touches it.
      </p>

      <ul className={styles.roleList}>
        {roles.map((role, index) => (
          <li key={role.name} className={styles.roleCard} data-testid={`${testIdPrefix}-role-${role.name}`}>
            <div className={styles.roleHeader}>
              <span className={styles.roleName}>{stageLabel(role.name)}</span>
              <div className={styles.roleControls}>
                <ZestButton
                  type="button"
                  onClick={() => moveRole(index, -1)}
                  disabled={index === 0}
                  data-testid={`${testIdPrefix}-role-${role.name}-up`}
                  zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
                >
                  Move up
                </ZestButton>
                <ZestButton
                  type="button"
                  onClick={() => moveRole(index, 1)}
                  disabled={index === roles.length - 1}
                  data-testid={`${testIdPrefix}-role-${role.name}-down`}
                  zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
                >
                  Move down
                </ZestButton>
                <ZestButton
                  type="button"
                  onClick={() => removeRole(index)}
                  data-testid={`${testIdPrefix}-role-${role.name}-remove`}
                  zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
                >
                  Remove
                </ZestButton>
              </div>
            </div>

            <label className={styles.inlineLabel}>
              <input
                type="checkbox"
                checked={role.writesCode}
                onChange={(e) => updateRole(index, { writesCode: e.target.checked })}
                data-testid={`${testIdPrefix}-role-${role.name}-writescode`}
              />
              Writes code (gets the feature&apos;s code paths, not just its docs)
            </label>

            <div className={styles.stepSummary} data-testid={`${testIdPrefix}-role-${role.name}-steps`}>
              Steps: {role.stepSummary.length > 0 ? role.stepSummary.join(" → ") : "(none yet — runs as a plain agent turn)"}
            </div>

            <GateStepListEditor
              label="Entry checks"
              hint="Must pass before this stage's turn starts."
              gates={role.entryGates}
              onChange={(gates) => updateRole(index, { entryGates: gates })}
              testIdPrefix={`${testIdPrefix}-role-${role.name}-entry`}
            />
            <GateStepListEditor
              label="Exit gate prompts"
              hint="LLM-graded checks that must pass before this stage can finish."
              gates={role.exitGatePrompts}
              onChange={(gates) => updateRole(index, { exitGatePrompts: gates })}
              testIdPrefix={`${testIdPrefix}-role-${role.name}-exit`}
            />
          </li>
        ))}
      </ul>

      <div className={styles.newRole}>
        <ZestTextbox
          data-testid={`${testIdPrefix}-new-name-input`}
          value={newName}
          onChange={(e) => setNewName(e.target.value)}
          placeholder="New stage name"
        />
        <ZestButton
          type="button"
          data-testid={`${testIdPrefix}-add-btn`}
          onClick={addRole}
          disabled={!newName.trim()}
          zest={{ semanticType: "add" }}
        >
          Add stage
        </ZestButton>
      </div>

      <ZestButton
        type="button"
        data-testid={`${testIdPrefix}-save-btn`}
        onClick={() => void handleSave()}
        disabled={loading || roles.length === 0}
        zest={{ semanticType: "save", busyOptions: { preventRageClick: true } }}
      >
        Save pipeline
      </ZestButton>
    </div>
  );
}

function GateStepListEditor({
  label, hint, gates, onChange, testIdPrefix,
}: {
  label: string;
  hint: string;
  gates: GateStepEditorDto[];
  onChange: (gates: GateStepEditorDto[]) => void;
  testIdPrefix: string;
}) {
  const [draftText, setDraftText] = useState("");
  const [draftSpecialist, setDraftSpecialist] = useState("");

  const addGate = () => {
    const text = draftText.trim();
    if (!text) return;
    onChange([...gates, { kind: "gatePrompt", gatePromptText: text, responsibleRole: null }]);
    setDraftText("");
  };

  const addSpecialistGate = () => {
    const name = draftSpecialist.trim();
    if (!name) return;
    onChange([...gates, { kind: "requiresSpecialist", requiredSpecialist: name, responsibleRole: null }]);
    setDraftSpecialist("");
  };

  const removeGate = (index: number) => onChange(gates.filter((_, i) => i !== index));

  const updateResponsibleRole = (index: number, responsibleRole: string) => {
    onChange(gates.map((g, i) => (i === index ? { ...g, responsibleRole: responsibleRole || null } : g)));
  };

  return (
    <div className={styles.gateEditor} data-testid={testIdPrefix}>
      <div className={styles.gateEditorLabel}>{label}</div>
      <div className={styles.gateEditorHint}>{hint}</div>
      {gates.length === 0 ? (
        <p className={styles.gateEditorEmpty}>None yet.</p>
      ) : (
        <ul className={styles.gateList}>
          {gates.map((gate, index) => (
            // eslint-disable-next-line react/no-array-index-key -- rows are add/remove only, never reordered
            <li key={index} className={styles.gateRow}>
              <span className={styles.gateText}>
                {gate.kind === "gatePrompt"
                  ? gate.gatePromptText
                  : gate.kind === "requiresSpecialist"
                    ? `specialist: ${gate.requiredSpecialist}`
                    : `builtin: ${gate.builtin}`}
              </span>
              <ZestTextbox
                data-testid={`${testIdPrefix}-${index}-responsible-role`}
                value={gate.responsibleRole ?? ""}
                onChange={(e) => updateResponsibleRole(index, e.target.value)}
                placeholder="Owning stage (defaults to this one)"
              />
              <ZestButton
                type="button"
                onClick={() => removeGate(index)}
                data-testid={`${testIdPrefix}-${index}-remove`}
                zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
              >
                Remove
              </ZestButton>
            </li>
          ))}
        </ul>
      )}
      <div className={styles.gateAddRow}>
        <ZestTextbox
          data-testid={`${testIdPrefix}-new-text`}
          value={draftText}
          onChange={(e) => setDraftText(e.target.value)}
          placeholder="New gate-prompt text…"
        />
        <ZestButton
          type="button"
          onClick={addGate}
          disabled={!draftText.trim()}
          data-testid={`${testIdPrefix}-add-btn`}
          zest={{ semanticType: "add" }}
        >
          Add
        </ZestButton>
      </div>
      <div className={styles.gateAddRow}>
        <ZestTextbox
          data-testid={`${testIdPrefix}-specialist-name`}
          value={draftSpecialist}
          onChange={(e) => setDraftSpecialist(e.target.value)}
          placeholder="Required specialist name…"
        />
        <ZestButton
          type="button"
          onClick={addSpecialistGate}
          disabled={!draftSpecialist.trim()}
          data-testid={`${testIdPrefix}-specialist-add-btn`}
          zest={{ semanticType: "add" }}
        >
          Add
        </ZestButton>
      </div>
    </div>
  );
}
