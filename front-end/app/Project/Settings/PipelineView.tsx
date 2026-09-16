"use client";
import React, { useCallback, useEffect, useRef, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ArtifactEditorDto, ArtifactRootKey, GateStepEditorDto, PipelineEditorRoleDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import ZestTextbox from "jattac.libs.web.zest-textbox";
import SelectWrapper from "../../Forms/SelectWrapper/UI/SelectWrapper";
import { stageLabel } from "../Release/labels";
import styles from "../Styles/PipelineView.module.css";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function emptyRole(name: string): PipelineEditorRoleDto {
  return { name, writesCode: false, signoff: null, userInputRequired: false, stepSummary: [], entryGates: [], exitGatePrompts: [], artifact: null };
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
  const artifactStageNames = roles.filter((r) => r.artifact != null).map((r) => r.name);
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
              artifactStageNames={artifactStageNames}
              specialistNames={specialistNames}
            />
            <GateStepListEditor
              label="Exit gate prompts"
              hint="LLM-graded checks that must pass before this stage can finish."
              gates={role.exitGatePrompts}
              onChange={(gates) => updateRole(index, { exitGatePrompts: gates })}
              testIdPrefix={`${testIdPrefix}-role-${role.name}-exit`}
              roleNames={roleNames}
              artifactStageNames={artifactStageNames}
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

// The full set of known placeholder tokens a gate-prompt author can reference — recomputed
// live from the current pipeline so a newly-declared artifact stage shows up immediately.
function placeholderTokens(artifactStageNames: string[]): string[] {
  return [
    "<F>",
    ...ARTIFACT_ROOT_OPTIONS.map((o) => `<${o.name}>`),
    ...artifactStageNames.map((name) => `<${name}/artifact.file>`),
  ];
}

function GateStepListEditor({
  label, hint, gates, onChange, testIdPrefix, roleNames, artifactStageNames, specialistNames,
}: {
  label: string;
  hint: string;
  gates: GateStepEditorDto[];
  onChange: (gates: GateStepEditorDto[]) => void;
  testIdPrefix: string;
  roleNames: string[];
  artifactStageNames: string[];
  specialistNames: string[];
}) {
  const [draftText, setDraftText] = useState("");

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
          tokens={placeholderTokens(artifactStageNames)}
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

interface IAutocompleteContext {
  start: number;
  end: number;
  query: string;
}

// A small, self-contained trigger-character ("<") autocomplete for a single-line input,
// plus an explicit "Insert placeholder" button so a novice who doesn't already know about
// the "<" convention can still discover and use it by browsing. Deliberately not a pulled-in
// library: the actual need — a closed set of ~10 known tokens — is narrow, and the one
// candidate library that fit was npm-deprecated with an unpatched vulnerability.
function PlaceholderAutocompleteInput({
  value, onChange, tokens, placeholder, testId,
}: {
  value: string;
  onChange: (value: string) => void;
  tokens: string[];
  placeholder?: string;
  testId: string;
}) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [context, setContext] = useState<IAutocompleteContext | null>(null);
  const [highlightIndex, setHighlightIndex] = useState(0);

  const detectTypedTrigger = (text: string, cursor: number): IAutocompleteContext | null => {
    const uptoCursor = text.slice(0, cursor);
    const openIndex = uptoCursor.lastIndexOf("<");
    if (openIndex === -1) return null;
    const between = uptoCursor.slice(openIndex + 1);
    if (between.includes(">") || /\s/.test(between)) return null;
    return { start: openIndex, end: cursor, query: between };
  };

  const matches = context ? tokens.filter((t) => t.toLowerCase().includes(context.query.toLowerCase())) : [];

  const applySuggestion = (token: string) => {
    if (!context) return;
    const before = value.slice(0, context.start);
    const after = value.slice(context.end);
    onChange(`${before}${token}${after}`);
    setContext(null);
    requestAnimationFrame(() => {
      const pos = before.length + token.length;
      inputRef.current?.focus();
      inputRef.current?.setSelectionRange(pos, pos);
    });
  };

  const openBrowseAll = () => {
    const cursor = inputRef.current?.selectionStart ?? value.length;
    setContext({ start: cursor, end: cursor, query: "" });
    setHighlightIndex(0);
    inputRef.current?.focus();
  };

  return (
    <div className={styles.autocompleteGroup}>
      <div className={styles.autocompleteWrapper}>
        <input
          ref={inputRef}
          type="text"
          className={styles.autocompleteInput}
          data-testid={testId}
          value={value}
          placeholder={placeholder}
          onChange={(e) => {
            onChange(e.target.value);
            setContext(detectTypedTrigger(e.target.value, e.target.selectionStart ?? e.target.value.length));
            setHighlightIndex(0);
          }}
          onKeyDown={(e) => {
            if (!context || matches.length === 0) return;
            if (e.key === "ArrowDown") { e.preventDefault(); setHighlightIndex((i) => (i + 1) % matches.length); }
            else if (e.key === "ArrowUp") { e.preventDefault(); setHighlightIndex((i) => (i - 1 + matches.length) % matches.length); }
            else if (e.key === "Enter" || e.key === "Tab") { e.preventDefault(); applySuggestion(matches[highlightIndex]); }
            else if (e.key === "Escape") { setContext(null); }
          }}
          // Deferred so a suggestion's onMouseDown (which calls preventDefault) still fires
          // before the list is torn down by this blur.
          onBlur={() => setTimeout(() => setContext(null), 150)}
        />
        {context && matches.length > 0 && (
          <ul className={styles.autocompleteList} data-testid={`${testId}-suggestions`}>
            {matches.map((token, i) => (
              <li
                key={token}
                className={i === highlightIndex ? styles.autocompleteOptionActive : styles.autocompleteOption}
                onMouseDown={(e) => { e.preventDefault(); applySuggestion(token); }}
              >
                {token}
              </li>
            ))}
          </ul>
        )}
      </div>
      <ZestButton
        type="button"
        onClick={openBrowseAll}
        data-testid={`${testId}-browse-btn`}
        zest={{ buttonStyle: "outline", visualOptions: { size: "sm" } }}
      >
        Insert placeholder
      </ZestButton>
    </div>
  );
}
