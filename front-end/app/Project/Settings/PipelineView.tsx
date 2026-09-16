"use client";
import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ArtifactEditorDto, ArtifactRootKey, GateStepEditorDto, PipelineEditorRoleDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import ZestTextbox from "jattac.libs.web.zest-textbox";
import SelectWrapper from "../../Forms/SelectWrapper/UI/SelectWrapper";
import PlaceholderAutocompleteInput, { placeholderTokens } from "../../Forms/PlaceholderAutocompleteInput/UI/PlaceholderAutocompleteInput";
import { stageLabel } from "../Release/labels";
import styles from "../Styles/PipelineView.module.css";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function emptyRole(name: string): PipelineEditorRoleDto {
  return { name, writesCode: false, signoff: null, userInputRequired: false, stepSummary: [], entryGates: [], exitGatePrompts: [], artifact: null, seedPrompt: null };
}

// A novice pipeline author types a human-readable title ("Code Mapping"); the stage's actual
// key — used everywhere else (requiresArtifact/requiresSpecialist targets, responsible-role
// pickers, placeholder tokens) — is auto-generated from it rather than hand-typed, so it's
// always well-formed and safe to pick from a dropdown elsewhere.
function slugify(title: string): string {
  const slug = title.trim().toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  return slug || "stage";
}

function uniqueStageKey(title: string, existingNames: string[]): string {
  const base = slugify(title);
  if (!existingNames.includes(base)) return base;
  let suffix = 2;
  while (existingNames.includes(`${base}-${suffix}`)) suffix += 1;
  return `${base}-${suffix}`;
}

// A stage's artifact filename is always derived from its own (already-unique) key plus its
// kind — never hand-typed — so the pipeline editor never offers a free-text field that could
// drift out of sync with the stage that owns it, or collide with another stage's artifact.
function artifactFileName(stageName: string, kind: "text" | "json"): string {
  return `${stageName}${kind === "json" ? ".json" : ".md"}`;
}

// react-select (via SelectWrapper) inspects each item with the `in` operator, which throws on
// primitive strings — options must be objects (see ProfilesView's IStageOption for the same
// convention).
interface INamedOption {
  name: string;
  label: string;
}

const ARTIFACT_ROOT_OPTIONS: INamedOption[] = [
  { name: "docs-root", label: "Docs root (workspace-wide)" },
  { name: "feature-docs-root", label: "Feature docs root" },
  { name: "feature-code-root-back", label: "Feature code root (back-end)" },
  { name: "feature-code-root-front", label: "Feature code root (front-end)" },
  { name: "workspace-root", label: "Workspace root" },
];

const RESPONSIBLE_ROLE_DEFAULT = "";

interface IPipelineViewProps {
  api: BrokerApi;
  workspacePath: string;
  testIdPrefix?: string;
}

export default function PipelineView({ api, workspacePath, testIdPrefix = "pipeline" }: IPipelineViewProps) {
  const [roles, setRoles] = useState<PipelineEditorRoleDto[]>([]);
  const [specialistNames, setSpecialistNames] = useState<string[]>([]);
  const [newTitle, setNewTitle] = useState("");
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [pipeline, specialists] = await Promise.all([
        api.getWorkspacePipelineAsync(workspacePath),
        api.getSpecialistsAsync(),
      ]);
      setRoles(pipeline.roles);
      setSpecialistNames(specialists.map((s) => s.name));
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
    const title = newTitle.trim();
    if (!title) return;
    const key = uniqueStageKey(title, roles.map((r) => r.name));
    setRoles((prev) => [...prev, emptyRole(key)]);
    setNewTitle("");
  }, [newTitle, roles]);

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

  const roleNames = roles.map((r) => r.name);
  const generatedKey = newTitle.trim() ? uniqueStageKey(newTitle, roleNames) : "";

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

            <div className={styles.stageInstructions}>
              <div className={styles.gateEditorLabel}>Stage instructions</div>
              <div className={styles.gateEditorHint}>
                What this stage should actually do — without this, it only gets a generic
                &quot;produce the required artifacts&quot; fallback. DevTeam automatically adds
                handoff/artifact context and a closing &quot;say DONE&quot; instruction — you
                don&apos;t need to repeat those.
              </div>
              <PlaceholderAutocompleteInput
                testId={`${testIdPrefix}-role-${role.name}-seed-prompt`}
                value={role.seedPrompt ?? ""}
                onChange={(value) => updateRole(index, { seedPrompt: value || null })}
                tokens={placeholderTokens(roles)}
                placeholder="e.g. Scan the codebase and emit <F>'s module graph to <docs-root>/<code-map/artifact.file>."
                multiline
              />
            </div>

            <div className={styles.stepSummary} data-testid={`${testIdPrefix}-role-${role.name}-steps`}>
              Steps: {role.stepSummary.length > 0 ? role.stepSummary.join(" → ") : "(none yet — runs as a plain agent turn)"}
            </div>

            <ArtifactEditor
              artifact={role.artifact ?? null}
              stageName={role.name}
              onChange={(artifact) => updateRole(index, { artifact })}
              testIdPrefix={`${testIdPrefix}-role-${role.name}`}
            />

            <GateStepListEditor
              label="Entry checks"
              hint="Must pass before this stage's turn starts."
              gates={role.entryGates}
              onChange={(gates) => updateRole(index, { entryGates: gates })}
              testIdPrefix={`${testIdPrefix}-role-${role.name}-entry`}
              roleNames={roleNames}
              roles={roles}
              specialistNames={specialistNames}
            />
            <GateStepListEditor
              label="Exit gate prompts"
              hint="LLM-graded checks that must pass before this stage can finish."
              gates={role.exitGatePrompts}
              onChange={(gates) => updateRole(index, { exitGatePrompts: gates })}
              testIdPrefix={`${testIdPrefix}-role-${role.name}-exit`}
              roleNames={roleNames}
              roles={roles}
              specialistNames={specialistNames}
            />
          </li>
        ))}
      </ul>

      <div className={styles.newRole}>
        <ZestTextbox
          data-testid={`${testIdPrefix}-new-name-input`}
          value={newTitle}
          onChange={(e) => setNewTitle(e.target.value)}
          placeholder="New stage title, e.g. Code Mapping"
        />
        {generatedKey && (
          <span className={styles.generatedKeyHint} data-testid={`${testIdPrefix}-new-key-preview`}>
            Key: {generatedKey}
          </span>
        )}
        <ZestButton
          type="button"
          data-testid={`${testIdPrefix}-add-btn`}
          onClick={addRole}
          disabled={!newTitle.trim()}
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

function ArtifactEditor({
  artifact, stageName, onChange, testIdPrefix,
}: {
  artifact: ArtifactEditorDto | null;
  stageName: string;
  onChange: (artifact: ArtifactEditorDto | null) => void;
  testIdPrefix: string;
}) {
  return (
    <div className={styles.artifactEditor} data-testid={`${testIdPrefix}-artifact`}>
      <label className={styles.inlineLabel}>
        <input
          type="checkbox"
          checked={artifact != null}
          onChange={(e) => onChange(
            e.target.checked
              ? { root: "docs-root", fileName: artifactFileName(stageName, "text"), kind: "text" }
              : null,
          )}
          data-testid={`${testIdPrefix}-artifact-toggle`}
        />
        Produces a shared artifact other stages can require/reference
      </label>
      {artifact && (
        <div className={styles.artifactFields}>
          <div className={styles.selectField} data-testid={`${testIdPrefix}-artifact-root`}>
            <SelectWrapper<INamedOption>
              data={ARTIFACT_ROOT_OPTIONS}
              selectedResolver={(o) => o.name === artifact.root}
              valueResolver={(o) => o.name}
              labelResolver={(o) => o.label}
              onChange={(items) => items[0] && onChange({ ...artifact, root: items[0].name as ArtifactRootKey })}
            />
          </div>
          <label className={styles.inlineLabel}>
            <input
              type="checkbox"
              checked={artifact.kind === "json"}
              onChange={(e) => {
                const kind = e.target.checked ? "json" : "text";
                onChange({ ...artifact, kind, fileName: artifactFileName(stageName, kind) });
              }}
              data-testid={`${testIdPrefix}-artifact-json-toggle`}
            />
            Validate as JSON (not just check it exists)
          </label>
          <span className={styles.generatedKeyHint} data-testid={`${testIdPrefix}-artifact-filename`}>
            File: {artifact.fileName}
          </span>
        </div>
      )}
    </div>
  );
}

function GateStepListEditor({
  label, hint, gates, onChange, testIdPrefix, roleNames, roles, specialistNames,
}: {
  label: string;
  hint: string;
  gates: GateStepEditorDto[];
  onChange: (gates: GateStepEditorDto[]) => void;
  testIdPrefix: string;
  roleNames: string[];
  roles: PipelineEditorRoleDto[];
  specialistNames: string[];
}) {
  const [draftText, setDraftText] = useState("");
  const artifactStageNames = roles.filter((r) => r.artifact != null).map((r) => r.name);

  const addGate = () => {
    const text = draftText.trim();
    if (!text) return;
    onChange([...gates, { kind: "gatePrompt", gatePromptText: text, responsibleRole: null }]);
    setDraftText("");
  };

  const removeGate = (index: number) => onChange(gates.filter((_, i) => i !== index));

  const updateResponsibleRole = (index: number, responsibleRole: string) => {
    onChange(gates.map((g, i) => (i === index ? { ...g, responsibleRole: responsibleRole || null } : g)));
  };

  const responsibleRoleOptions: INamedOption[] = [
    { name: RESPONSIBLE_ROLE_DEFAULT, label: "(this stage)" },
    ...roleNames.map((name) => ({ name, label: stageLabel(name) })),
  ];

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
                    : gate.kind === "requiresArtifact"
                      ? `artifact from: ${gate.requiredArtifactStage}`
                      : `builtin: ${gate.builtin}`}
              </span>
              <div className={styles.selectField} data-testid={`${testIdPrefix}-${index}-responsible-role`}>
                <SelectWrapper<INamedOption>
                  data={responsibleRoleOptions}
                  selectedResolver={(o) => o.name === (gate.responsibleRole ?? RESPONSIBLE_ROLE_DEFAULT)}
                  valueResolver={(o) => o.name}
                  labelResolver={(o) => o.label}
                  onChange={(items) => updateResponsibleRole(index, items[0]?.name ?? RESPONSIBLE_ROLE_DEFAULT)}
                />
              </div>
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
        <PlaceholderAutocompleteInput
          testId={`${testIdPrefix}-new-text`}
          value={draftText}
          onChange={setDraftText}
          tokens={placeholderTokens(roles)}
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
      <div className={styles.gateEditorHint}>
        Tip: type &lt; in the box above for known placeholders like &lt;F&gt; or &lt;docs-root&gt;, or click &quot;Insert placeholder&quot; to browse them all.
      </div>
      <div className={styles.gateAddRow} data-testid={`${testIdPrefix}-specialist-picker`}>
        <div className={styles.selectField}>
          <SelectWrapper<INamedOption>
            data={specialistNames.map((name) => ({ name, label: name }))}
            selectedResolver={() => false}
            valueResolver={(o) => o.name}
            labelResolver={(o) => o.label}
            placeholder="Add a required specialist…"
            onChange={(items) => {
              if (items[0]) onChange([...gates, { kind: "requiresSpecialist", requiredSpecialist: items[0].name, responsibleRole: null }]);
            }}
          />
        </div>
      </div>
      <div className={styles.gateAddRow} data-testid={`${testIdPrefix}-artifact-picker`}>
        <div className={styles.selectField}>
          <SelectWrapper<INamedOption>
            data={artifactStageNames.map((name) => ({ name, label: stageLabel(name) }))}
            selectedResolver={() => false}
            valueResolver={(o) => o.name}
            labelResolver={(o) => o.label}
            placeholder="Require another stage's artifact…"
            onChange={(items) => {
              if (items[0]) onChange([...gates, { kind: "requiresArtifact", requiredArtifactStage: items[0].name, responsibleRole: null }]);
            }}
          />
        </div>
      </div>
    </div>
  );
}

