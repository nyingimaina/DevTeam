"use client";
import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { GitStatusDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/GitView.module.css";

interface IGitViewProps {
  api: BrokerApi;
  workspacePath: string;
}

export default function GitView({ api, workspacePath }: IGitViewProps) {
  const [status, setStatus] = useState<GitStatusDto | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [commitMessage, setCommitMessage] = useState("");
  const [newBranch, setNewBranch] = useState("");

  const loadStatus = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const result = await api.getGitStatusAsync(workspacePath);
      setStatus(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath]);

  useEffect(() => {
    void loadStatus();
  }, [loadStatus]);

  const handleInit = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const result = await api.initGitAsync(workspacePath);
      setStatus(result);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath]);

  const handleCommit = useCallback(async () => {
    if (!commitMessage.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const result = await api.commitGitAsync(workspacePath, commitMessage.trim());
      setStatus(result);
      setCommitMessage("");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath, commitMessage]);

  const handleCreateBranch = useCallback(async () => {
    if (!newBranch.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const result = await api.createGitBranchAsync(workspacePath, newBranch.trim());
      setStatus(result);
      setNewBranch("");
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath, newBranch]);

  if (!status && loading) {
    return <div className={styles.container}>Loading git status...</div>;
  }

  if (status && !status.isRepo) {
    return (
      <div className={styles.container}>
        <div className={styles.notRepo}>
          <div className={styles.notRepoIcon}>📂</div>
          <div>Not a git repository</div>
          <button onClick={handleInit} disabled={loading} className={styles.actionBtn}>
            Initialize Git
          </button>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.container} data-testid="git-view">
      {error && <div className={styles.error}>{error}</div>}

      {/* Branch header */}
      <div className={styles.branchHeader}>
        <span className={styles.branchIcon}>🌿</span>
        <span className={styles.branchName}>{status?.branch ?? "—"}</span>
        {status && !status.isClean && (
          <span className={styles.dirtyBadge}>
            +{status.ahead} −{status.behind}
          </span>
        )}
        {status?.isClean && <span className={styles.cleanBadge}>clean</span>}
      </div>

      {/* Actions */}
      <div className={styles.actions}>
        <div className={styles.actionRow}>
          <input
            value={commitMessage}
            onChange={(e) => setCommitMessage(e.target.value)}
            placeholder="Commit message..."
            className={styles.input}
            onKeyDown={(e) => e.key === "Enter" && handleCommit()}
            disabled={loading}
          />
          <button
            onClick={handleCommit}
            disabled={loading || !commitMessage.trim()}
            className={styles.actionBtn}
          >
            Commit
          </button>
        </div>

        <div className={styles.actionRow}>
          <input
            value={newBranch}
            onChange={(e) => setNewBranch(e.target.value)}
            placeholder="New branch name..."
            className={styles.input}
            onKeyDown={(e) => e.key === "Enter" && handleCreateBranch()}
            disabled={loading}
          />
          <button
            onClick={handleCreateBranch}
            disabled={loading || !newBranch.trim()}
            className={styles.actionBtn}
          >
            Create Branch
          </button>
        </div>

        <button onClick={loadStatus} disabled={loading} className={styles.refreshBtn}>
          Refresh
        </button>
      </div>

      {/* Branches */}
      {status?.branches && status.branches.length > 0 && (
        <div className={styles.branchesSection}>
          <h3>Branches</h3>
          <div className={styles.branchList}>
            {status.branches.map((b) => (
              <div
                key={b}
                className={`${styles.branchItem} ${b === status.branch ? styles.branchActive : ""}`}
              >
                {b === status.branch && <span className={styles.currentMarker}>●</span>}
                {b}
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
