"use client";
import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import ZestButton from "jattac.libs.web.zest-button";
import styles from "../Styles/ManageRepositoryPane.module.css";

interface IManageRepositoryPaneProps {
  api: BrokerApi;
  workspacePath: string;
  onClose: () => void;
}

const NEW_CREDENTIAL_OPTION = "__new__";

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export default function ManageRepositoryPane({ api, workspacePath, onClose }: IManageRepositoryPaneProps) {
  const [remoteUrl, setRemoteUrl] = useState("");
  const [credentialNames, setCredentialNames] = useState<string[]>([]);
  const [selectedCredential, setSelectedCredential] = useState("");
  const [newCredentialName, setNewCredentialName] = useState("");
  const [newCredentialToken, setNewCredentialToken] = useState("");
  const [savingRemote, setSavingRemote] = useState(false);
  const [savingCredential, setSavingCredential] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [remote, credentials] = await Promise.all([
          api.getGitRemoteAsync(workspacePath),
          api.listGitCredentialsAsync(),
        ]);
        if (cancelled) return;
        setRemoteUrl(remote.url ?? "");
        setCredentialNames(credentials.names);
        setSelectedCredential(remote.credentialName ?? "");
      } catch (e) {
        if (!cancelled) setError(toErrorMessage(e));
      }
    })();
    return () => { cancelled = true; };
  }, [api, workspacePath]);

  const handleSaveRemote = useCallback(async () => {
    setSavingRemote(true);
    setError(null);
    try {
      const credentialName = selectedCredential === NEW_CREDENTIAL_OPTION || selectedCredential === ""
        ? null
        : selectedCredential;
      await api.setGitRemoteAsync(workspacePath, remoteUrl.trim(), credentialName);
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setSavingRemote(false);
    }
  }, [api, workspacePath, remoteUrl, selectedCredential]);

  const handleSaveCredential = useCallback(async () => {
    if (!newCredentialName.trim() || !newCredentialToken.trim()) return;
    setSavingCredential(true);
    setError(null);
    try {
      await api.setGitCredentialAsync(newCredentialName.trim(), newCredentialToken.trim());
      setCredentialNames((prev) => [...prev, newCredentialName.trim()]);
      setSelectedCredential(newCredentialName.trim());
      setNewCredentialName("");
      setNewCredentialToken("");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setSavingCredential(false);
    }
  }, [api, newCredentialName, newCredentialToken]);

  const addingNew = selectedCredential === NEW_CREDENTIAL_OPTION;

  return (
    <div className={styles.pane} data-testid="manage-repo-pane">
      <div className={styles.header}>
        <h3>Manage Repository</h3>
        <ZestButton
          type="button"
          onClick={onClose}
          data-testid="manage-repo-close-btn"
          zest={{ buttonStyle: "text", visualOptions: { size: "sm" } }}
        >
          Close
        </ZestButton>
      </div>

      {error && <div className={styles.error}>{error}</div>}

      <div className={styles.section}>
        <label className={styles.label} htmlFor="manage-repo-remote-url">Remote URL</label>
        <input
          id="manage-repo-remote-url"
          data-testid="manage-repo-remote-url"
          className={styles.input}
          value={remoteUrl}
          onChange={(e) => setRemoteUrl(e.target.value)}
          placeholder="https://github.com/org/repo.git"
        />

        <label className={styles.label} htmlFor="manage-repo-credential-select">Personal access token (optional)</label>
        <select
          id="manage-repo-credential-select"
          data-testid="manage-repo-credential-select"
          className={styles.input}
          value={selectedCredential}
          onChange={(e) => setSelectedCredential(e.target.value)}
        >
          <option value="">(none)</option>
          {credentialNames.map((name) => (
            <option key={name} value={name}>{name}</option>
          ))}
          <option value={NEW_CREDENTIAL_OPTION}>+ Add new PAT...</option>
        </select>

        {addingNew && (
          <div className={styles.newCredential}>
            <input
              data-testid="manage-repo-new-credential-name"
              className={styles.input}
              value={newCredentialName}
              onChange={(e) => setNewCredentialName(e.target.value)}
              placeholder="Name (e.g. github-personal)"
            />
            <input
              data-testid="manage-repo-new-credential-token"
              className={styles.input}
              type="password"
              value={newCredentialToken}
              onChange={(e) => setNewCredentialToken(e.target.value)}
              placeholder="Personal access token"
            />
            <ZestButton
              type="button"
              onClick={handleSaveCredential}
              disabled={savingCredential || !newCredentialName.trim() || !newCredentialToken.trim()}
              data-testid="manage-repo-save-credential-btn"
              zest={{ semanticType: "save", busyOptions: { preventRageClick: true }, visualOptions: { size: "sm" } }}
            >
              {savingCredential ? "Saving..." : "Save Credential"}
            </ZestButton>
          </div>
        )}

        <ZestButton
          type="button"
          onClick={handleSaveRemote}
          disabled={savingRemote}
          data-testid="manage-repo-save-remote-btn"
          zest={{ semanticType: "save", busyOptions: { preventRageClick: true } }}
        >
          {savingRemote ? "Saving..." : "Save Remote"}
        </ZestButton>
      </div>
    </div>
  );
}
