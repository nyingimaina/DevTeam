"use client";
import React, { useCallback, useEffect, useMemo, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { GitCommitDto, GitStatusDto } from "../../Chat/Data/BrokerTypes";
import ZestButton from "jattac.libs.web.zest-button";
import ManageRepositoryPane from "./ManageRepositoryPane";
import styles from "../Styles/GitView.module.css";

interface IGitViewProps {
  api: BrokerApi;
  workspacePath: string;
}

export default function GitView({ api, workspacePath }: IGitViewProps) {
  const [status, setStatus] = useState<GitStatusDto | null>(null);
  const [log, setLog] = useState<GitCommitDto[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [commitMessage, setCommitMessage] = useState("");
  const [newBranch, setNewBranch] = useState("");
  const [view, setView] = useState<"graph" | "branches">("graph");
  const [manageRepoOpen, setManageRepoOpen] = useState(false);

  const loadAll = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [statusResult, logResult] = await Promise.all([
        api.getGitStatusAsync(workspacePath),
        api.getGitLogAsync(workspacePath),
      ]);
      setStatus(statusResult);
      setLog(logResult.commits ?? []);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [api, workspacePath]);

  useEffect(() => { void loadAll(); }, [loadAll]);

  const handleInit = useCallback(async () => {
    setError(null);
    await api.initGitAsync(workspacePath);
    await loadAll();
  }, [api, workspacePath, loadAll]);

  const handleCommit = useCallback(async () => {
    if (!commitMessage.trim()) return;
    setError(null);
    await api.commitGitAsync(workspacePath, commitMessage.trim());
    setCommitMessage("");
    await loadAll();
  }, [api, workspacePath, commitMessage, loadAll]);

  const handleCreateBranch = useCallback(async () => {
    if (!newBranch.trim()) return;
    setError(null);
    await api.createGitBranchAsync(workspacePath, newBranch.trim());
    setNewBranch("");
    await loadAll();
  }, [api, workspacePath, newBranch, loadAll]);

  if (!status && loading) {
    return <div className={styles.container}>Loading git status...</div>;
  }

  if (status && !status.isRepo) {
    return (
      <div className={styles.container}>
        <div className={styles.notRepo}>
          <div className={styles.notRepoIcon}>📂</div>
          <div>Not a git repository</div>
          <ZestButton
            type="button"
            onClick={handleInit}
            zest={{
              semanticType: "add",
              busyOptions: { preventRageClick: true },
            }}
          >
            Initialize Git
          </ZestButton>
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
          <span className={styles.dirtyBadge}>dirty</span>
        )}
        {status?.isClean && <span className={styles.cleanBadge}>clean</span>}
        <ZestButton
          type="button"
          onClick={() => setManageRepoOpen(true)}
          data-testid="git-manage-repo-btn"
          className={styles.manageRepoBtn}
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}
        >
          Manage Repository
        </ZestButton>
      </div>

      {manageRepoOpen && (
        <ManageRepositoryPane
          api={api}
          workspacePath={workspacePath}
          onClose={() => setManageRepoOpen(false)}
        />
      )}

      {/* View toggle */}
      <div className={styles.viewToggle}>
        <ZestButton
          type="button"
          onClick={() => setView("graph")}
          className={styles.toggleBtn}
          zest={{
            buttonStyle: view === "graph" ? "solid" : "text",
            visualOptions: { size: "sm" },
          }}
        >
          Graph
        </ZestButton>
        <ZestButton
          type="button"
          onClick={() => setView("branches")}
          className={styles.toggleBtn}
          zest={{
            buttonStyle: view === "branches" ? "solid" : "text",
            visualOptions: { size: "sm" },
          }}
        >
          Branches
        </ZestButton>
      </div>

      {view === "graph" && (
        <div className={styles.graphContainer}>
          <CommitGraph commits={log} currentBranch={status?.branch} />
        </div>
      )}

      {view === "branches" && (
        <div className={styles.branchesSection}>
          <div className={styles.branchList}>
            {(status?.branches ?? []).map((b) => (
              <div
                key={b}
                className={`${styles.branchItem} ${b === status?.branch ? styles.branchActive : ""}`}
              >
                {b === status?.branch && <span className={styles.currentMarker}>●</span>}
                {b}
              </div>
            ))}
          </div>
        </div>
      )}

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
          <ZestButton
            type="button"
            onClick={handleCommit}
            disabled={!commitMessage.trim()}
            zest={{
              semanticType: "save",
              busyOptions: { preventRageClick: true },
              visualOptions: { size: "sm" },
            }}
          >
            Commit
          </ZestButton>
        </div>
        <div className={styles.actionRow}>
          <input
            value={newBranch}
            onChange={(e) => setNewBranch(e.target.value)}
            placeholder="New branch..."
            className={styles.input}
            onKeyDown={(e) => e.key === "Enter" && handleCreateBranch()}
            disabled={loading}
          />
          <ZestButton
            type="button"
            onClick={handleCreateBranch}
            disabled={!newBranch.trim()}
            zest={{
              semanticType: "add",
              busyOptions: { preventRageClick: true },
              visualOptions: { size: "sm" },
            }}
          >
            Branch
          </ZestButton>
        </div>
        <ZestButton
          type="button"
          onClick={loadAll}
          zest={{
            semanticType: "refresh",
            busyOptions: { preventRageClick: true },
            buttonStyle: "text",
            visualOptions: { size: "sm" },
          }}
        >
          Refresh
        </ZestButton>
      </div>
    </div>
  );
}

// ─── commit graph component ─────────────────────────────────────────────

interface CommitGraphProps {
  commits: GitCommitDto[];
  currentBranch?: string | null;
}

function CommitGraph({ commits, currentBranch }: CommitGraphProps) {
  const [expanded, setExpanded] = useState(false);
  const visible = expanded ? commits : commits.slice(0, 20);

  return (
    <div className={styles.commitList}>
      {visible.map((commit, i) => (
        <CommitRow key={commit.hash} commit={commit} isHead={i === 0} />
      ))}
      {commits.length > 20 && !expanded && (
        <ZestButton
          type="button"
          onClick={() => setExpanded(true)}
          className={styles.showMore}
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}
        >
          Show all {commits.length} commits...
        </ZestButton>
      )}
    </div>
  );
}

interface CommitRowProps {
  commit: GitCommitDto;
  isHead: boolean;
}

function CommitRow({ commit, isHead }: CommitRowProps) {
  const date = useMemo(() => {
    try {
      const d = new Date(commit.date);
      const now = new Date();
      const diffMs = now.getTime() - d.getTime();
      const diffDays = Math.floor(diffMs / 86400000);
      if (diffDays === 0) return "today";
      if (diffDays === 1) return "yesterday";
      if (diffDays < 7) return `${diffDays}d ago`;
      return d.toLocaleDateString();
    } catch {
      return commit.date;
    }
  }, [commit.date]);

  return (
    <div className={styles.commitRow}>
      <div className={styles.commitNode}>
        <div className={`${styles.commitDot} ${isHead ? styles.commitDotHead : ""}`} />
        <div className={styles.commitLine} />
      </div>
      <div className={styles.commitContent}>
        <div className={styles.commitMessage}>
          {commit.message}
          {commit.tags?.map((t) => (
            <span key={t} className={styles.commitTag}>{t}</span>
          ))}
        </div>
        <div className={styles.commitMeta}>
          <span className={styles.commitHash}>{commit.shortHash}</span>
          <span className={styles.commitAuthor}>{commit.author}</span>
          <span className={styles.commitDate}>{date}</span>
        </div>
      </div>
    </div>
  );
}
