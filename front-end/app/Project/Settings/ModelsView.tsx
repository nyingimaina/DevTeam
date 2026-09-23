"use client";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ModelCandidateDto, ModelOption } from "../../Chat/Data/BrokerTypes";
import { toErrorMessage } from "../Release/ReleaseWizard";
import styles from "../Styles/Models.module.css";

interface IModelsViewProps {
  api: BrokerApi;
  workspacePath: string;
}

// The list an admin can reason about: position IS the order the models are tried in, and a
// cooling-down entry says why it was skipped — so a run that is slow to start explains itself.
export default function ModelsView({ api, workspacePath }: IModelsViewProps) {
  const [candidates, setCandidates] = useState<ModelCandidateDto[]>([]);
  const [available, setAvailable] = useState<ModelOption[]>([]);
  const [newModel, setNewModel] = useState("");
  const [loading, setLoading] = useState(true);
  const [busyId, setBusyId] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [, tick] = useState(0);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [list, catalog] = await Promise.all([
        api.listModelCandidatesAsync(workspacePath),
        api.listAvailableModelsAsync(workspacePath).catch(() => [] as ModelOption[]),
      ]);
      setCandidates(list);
      setAvailable(catalog);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath]);

  useEffect(() => {
    void load();
  }, [load]);

  // Cooldowns expire on their own, so keep the "cooling down" wording honest without a reload.
  useEffect(() => {
    const timer = window.setInterval(() => tick((t) => t + 1), 15000);
    return () => window.clearInterval(timer);
  }, []);

  const move = useCallback(async (id: string, direction: "up" | "down") => {
    const index = candidates.findIndex((c) => c.id === id);
    const target = direction === "up" ? index - 1 : index + 1;
    if (index < 0 || target < 0 || target >= candidates.length) return;

    const reordered = [...candidates];
    [reordered[index], reordered[target]] = [reordered[target], reordered[index]];
    setCandidates(reordered);
    setBusyId(id);
    setError(null);
    try {
      setCandidates(await api.reorderModelCandidatesAsync(workspacePath, reordered.map((c) => c.id)));
    } catch (e) {
      setError(toErrorMessage(e));
      await load();
    } finally {
      setBusyId(null);
    }
  }, [api, candidates, load, workspacePath]);

  const toggleEnabled = useCallback(async (candidate: ModelCandidateDto) => {
    setBusyId(candidate.id);
    setError(null);
    try {
      await api.setModelCandidateEnabledAsync(candidate.id, !candidate.enabled);
      await load();
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setBusyId(null);
    }
  }, [api, load]);

  const remove = useCallback(async (candidate: ModelCandidateDto) => {
    setBusyId(candidate.id);
    setError(null);
    try {
      await api.removeModelCandidateAsync(candidate.id);
      await load();
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setBusyId(null);
    }
  }, [api, load]);

  const add = useCallback(async () => {
    const modelId = newModel.trim();
    if (!modelId) return;

    setAdding(true);
    setError(null);
    try {
      await api.addModelCandidateAsync(workspacePath, modelId);
      setNewModel("");
      await load();
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setAdding(false);
    }
  }, [api, load, newModel, workspacePath]);

  const coolingCount = useMemo(
    () => candidates.filter((c) => isCoolingDown(c)).length,
    [candidates],
  );

  return (
    <div className={styles.view} data-testid="models-view">
      <h2 className={styles.title}>Models</h2>
      <p className={styles.intro}>
        The order the AI models are tried in. If one is refused — for example rate-limited — the
        next one in this list takes over automatically, so work keeps moving.
      </p>

      {error && (
        <div className={styles.error} data-testid="models-error">
          {error}
        </div>
      )}

      {coolingCount > 0 && (
        <div className={styles.coolingNotice} data-testid="models-cooling-notice">
          {coolingCount} model{coolingCount === 1 ? " is" : "s are"} cooling down after being
          refused. That is why a run may pause before starting.
        </div>
      )}

      {loading ? (
        <div className={styles.muted}>Loading…</div>
      ) : (
        <ul className={styles.list} data-testid="models-list">
          {candidates.map((candidate, index) => (
            <li key={candidate.id} className={styles.row} data-testid={`model-row-${candidate.modelId}`}>
              <span className={styles.rank}>#{index + 1}</span>
              <div className={styles.main}>
                <div className={styles.modelId}>{candidate.modelId}</div>
                <div className={styles.meta}>
                  {candidate.enabled ? (
                    <span className={styles.enabled}>Enabled</span>
                  ) : (
                    <span className={styles.disabled}>Disabled</span>
                  )}
                  {isCoolingDown(candidate) && (
                    <span className={styles.cooling} data-testid={`model-cooling-${candidate.modelId}`}>
                      Cooling down until {formatTime(candidate.cooldownUntil!)} —{" "}
                      {candidate.lastFailureReason ?? "the AI service refused it"}
                    </span>
                  )}
                </div>
                <div className={styles.stats} data-testid={`model-stats-${candidate.modelId}`}>
                  {candidate.cost == null || candidate.smartness == null ? (
                    <span className={styles.unknown}>Stats: unknown</span>
                  ) : (
                    <span>
                      Cost {candidate.cost}/5 · Capability {candidate.smartness}/5
                      {candidate.note ? ` · ${candidate.note}` : ""}
                    </span>
                  )}
                </div>
              </div>
              <div className={styles.actions}>
                <button
                  type="button"
                  className={styles.iconButton}
                  disabled={index === 0 || busyId === candidate.id}
                  onClick={() => void move(candidate.id, "up")}
                  aria-label="Move up"
                  data-testid={`model-up-${candidate.modelId}`}
                >
                  ↑
                </button>
                <button
                  type="button"
                  className={styles.iconButton}
                  disabled={index === candidates.length - 1 || busyId === candidate.id}
                  onClick={() => void move(candidate.id, "down")}
                  aria-label="Move down"
                  data-testid={`model-down-${candidate.modelId}`}
                >
                  ↓
                </button>
                <button
                  type="button"
                  className={styles.secondary}
                  disabled={busyId === candidate.id}
                  onClick={() => void toggleEnabled(candidate)}
                  data-testid={`model-toggle-${candidate.modelId}`}
                >
                  {candidate.enabled ? "Disable" : "Enable"}
                </button>
                <button
                  type="button"
                  className={styles.secondary}
                  disabled={busyId === candidate.id}
                  onClick={() => void remove(candidate)}
                  data-testid={`model-remove-${candidate.modelId}`}
                >
                  Remove
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}

      <section className={styles.addSection}>
        <label className={styles.addLabel} htmlFor="model-add-input">
          Add a model
        </label>
        <div className={styles.addRow}>
          <input
            id="model-add-input"
            className={styles.input}
            list="available-models"
            placeholder="provider/model"
            value={newModel}
            onChange={(e) => setNewModel(e.target.value)}
            data-testid="model-add-input"
          />
          <datalist id="available-models">
            {available.map((option) => (
              <option key={option.value} value={option.value} />
            ))}
          </datalist>
          <button
            type="button"
            className={styles.primary}
            disabled={adding || !newModel.trim()}
            onClick={() => void add()}
            data-testid="model-add-btn"
          >
            {adding ? "Adding…" : "Add"}
          </button>
        </div>
      </section>
    </div>
  );
}

function isCoolingDown(candidate: ModelCandidateDto): boolean {
  if (!candidate.cooldownUntil) return false;
  return Date.parse(candidate.cooldownUntil) > Date.now();
}

function formatTime(iso: string): string {
  const at = new Date(iso);
  return Number.isNaN(at.getTime())
    ? "shortly"
    : at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}
