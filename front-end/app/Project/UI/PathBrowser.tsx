import React, { useCallback, useEffect, useMemo, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import {
  FileSystemEntryDto,
  FileSystemRootDto,
} from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/PathBrowser.module.css";

export type PathBrowserMode = "pickDirectory" | "pickFile";

interface IPathBrowserProps {
  api: BrokerApi;
  mode?: PathBrowserMode;
  selectedPath?: string | null;
  onSelect: (path: string, kind: "directory" | "file") => void;
  testIdPrefix?: string;
}

interface Crumb {
  label: string;
  fullPath: string;
}

function toErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function buildCrumbs(currentPath: string, roots: FileSystemRootDto[]): Crumb[] {
  const rootsByLength = [...roots].sort((a, b) => b.path.length - a.path.length);
  const root = rootsByLength.find(
    (candidate) =>
      currentPath === candidate.path ||
      currentPath.toLowerCase().startsWith(candidate.path.toLowerCase() + "\\"),
  );

  if (!root) {
    const crumbs: Crumb[] = [];
    let prefix = "";
    for (const segment of currentPath.split(/[\\/]+/)) {
      if (!segment) continue;
      prefix = prefix ? `${prefix}\\${segment}` : segment;
      crumbs.push({ label: segment, fullPath: prefix });
    }
    return crumbs;
  }

  const crumbs: Crumb[] = [{ label: root.displayName, fullPath: root.path }];
  let prefix = root.path;
  const remainder = currentPath.slice(root.path.length).replace(/^[\\/]+/, "");
  for (const segment of remainder.split(/[\\/]+/)) {
    if (!segment) continue;
    prefix = `${prefix}\\${segment}`;
    crumbs.push({ label: segment, fullPath: prefix });
  }
  return crumbs;
}

function joinPath(parent: string, name: string): string {
  return `${parent.replace(/[\\/]+$/, "")}\\${name}`;
}

export default function PathBrowser({
  api,
  mode = "pickDirectory",
  selectedPath,
  onSelect,
  testIdPrefix = "pathbrowser",
}: IPathBrowserProps) {
  const [roots, setRoots] = useState<FileSystemRootDto[]>([]);
  const [entries, setEntries] = useState<FileSystemEntryDto[]>([]);
  const [currentPath, setCurrentPath] = useState("");
  const [currentIsDirectory, setCurrentIsDirectory] = useState(true);
  const [gitRepository, setGitRepository] = useState(false);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState("");
  const [creating, setCreating] = useState(false);
  const [newFolderName, setNewFolderName] = useState("");
  const [error, setError] = useState<string | null>(null);

  const navigateTo = useCallback(
    async (path: string) => {
      setLoading(true);
      setError(null);
      try {
        const [stat, listing] = await Promise.all([
          api.getFileSystemStatAsync(path),
          api.listDirectoryAsync(path),
        ]);
        if (!stat.exists) throw new Error(`Path does not exist: ${path}`);
        setCurrentPath(path);
        setCurrentIsDirectory(stat.kind === "directory");
        setGitRepository(stat.isGitRepository);
        setEntries(listing);
        setFilter("");
      } catch (caught) {
        setError(toErrorMessage(caught));
      } finally {
        setLoading(false);
      }
    },
    [api],
  );

  useEffect(() => {
    let disposed = false;
    (async () => {
      try {
        const availableRoots = await api.listFileSystemRootsAsync();
        if (disposed) return;
        setRoots(availableRoots);
        const initial = availableRoots.find((r) => r.displayName === "Home") ?? availableRoots[0];
        if (initial) await navigateTo(initial.path);
        else setLoading(false);
      } catch (caught) {
        if (!disposed) {
          setLoading(false);
          setError(toErrorMessage(caught));
        }
      }
    })();
    return () => {
      disposed = true;
    };
  }, [api, navigateTo]);

  const crumbs = useMemo(() => buildCrumbs(currentPath, roots), [currentPath, roots]);

  const filteredEntries = useMemo(() => {
    const needle = filter.trim().toLowerCase();
    if (!needle) return entries;
    return entries.filter((entry) => entry.name.toLowerCase().includes(needle));
  }, [entries, filter]);

  const handleEntryClick = (entry: FileSystemEntryDto) => {
    if (entry.kind === "directory") {
      void navigateTo(entry.fullPath);
    } else if (mode === "pickFile") {
      onSelect(entry.fullPath, "file");
    }
  };

  const handlePromotePath = () => {
    const normalized = filter.trim();
    if (normalized) void navigateTo(normalized);
  };

  const handleChooseCurrent = () => {
    if (!currentPath) return;
    onSelect(currentPath, currentIsDirectory ? "directory" : "file");
  };

  const handleEnableCreating = () => {
    setCreating(true);
    setNewFolderName("");
  };

  const handleCreateFolder = async () => {
    const name = newFolderName.trim();
    if (!name || !currentPath) return;
    try {
      await api.createDirectoryAsync(joinPath(currentPath, name));
      setCreating(false);
      setNewFolderName("");
      await navigateTo(currentPath);
    } catch (caught) {
      setError(toErrorMessage(caught));
    }
  };

  const hint =
    mode === "pickDirectory" ? "Pick a folder to use as the project workspace" : "Pick the file to open";

  return (
    <div className={styles.browser} data-testid={testIdPrefix}>
      <div className={styles.header}>
        <div className={styles.title}>Browse</div>
        <div className={styles.hint}>{hint}</div>
      </div>

      <div className={styles.roots} aria-label="Quick locations">
        {roots.map((root) => (
          <button
            key={root.path}
            type="button"
            className={styles.rootChip}
            onClick={() => void navigateTo(root.path)}
          >
            {root.displayName}
          </button>
        ))}
      </div>

      <div className={styles.locationBar}>
        <input
          className={styles.pathInput}
          value={currentPath}
          aria-label="Path"
          placeholder="Type or edit a path, then press Enter"
          onChange={(e) => setCurrentPath(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void navigateTo(currentPath);
          }}
        />
        {gitRepository && <span className={styles.gitBadge} title="This is a git repository">git</span>}
      </div>

      <nav className={styles.crumbs} aria-label="Breadcrumb">
        {crumbs.map((crumb, index) => (
          <React.Fragment key={`${crumb.fullPath}-${index}`}>
            {index > 0 && <span className={styles.crumbSep}>/</span>}
            <button
              type="button"
              className={styles.crumb}
              aria-label={`Go to ${crumb.label}`}
              onClick={() => void navigateTo(crumb.fullPath)}
            >
              {crumb.label}
            </button>
          </React.Fragment>
        ))}
      </nav>

      <div className={styles.toolbar}>
        <input
          className={styles.filterInput}
          value={filter}
          aria-label="Filter entries"
          placeholder="Filter…"
          onChange={(e) => setFilter(e.target.value)}
        />
        {!creating && (
          <button type="button" className={styles.newFolder} onClick={handleEnableCreating}>
            New folder
          </button>
        )}
      </div>

      {creating && (
        <div className={styles.createRow}>
          <input
            className={styles.newNameInput}
            value={newFolderName}
            aria-label="New folder name"
            autoFocus
            onChange={(e) => setNewFolderName(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void handleCreateFolder();
            }}
          />
          <button type="button" className={styles.createBtn} onClick={() => void handleCreateFolder()}>
            Create
          </button>
          <button type="button" className={styles.cancelBtn} onClick={() => setCreating(false)}>
            Cancel
          </button>
        </div>
      )}

      {error && (
        <div className={styles.error} role="alert">
          {error}
        </div>
      )}

      {loading ? (
        <div className={styles.status} role="status">
          Loading…
        </div>
      ) : (
        <ul className={styles.list}>
          {filteredEntries.map((entry) => {
            const isDirectory = entry.kind === "directory";
            const selectable = isDirectory || mode === "pickFile";
            return (
              <li key={entry.fullPath}>
                <button
                  type="button"
                  className={styles.row}
                  disabled={!selectable}
                  data-testid={`${testIdPrefix}-entry-${entry.name}`}
                  onClick={() => handleEntryClick(entry)}
                  aria-label={isDirectory ? `Open folder ${entry.name}` : `Select file ${entry.name}`}
                >
                  <span className={isDirectory ? styles.dirMarker : styles.fileMarker}>
                    {isDirectory ? "Folder" : "File"}
                  </span>
                  <span className={styles.entryName}>{entry.name}</span>
                  <span className={styles.entryMeta}>{formatSize(entry.sizeBytes)}</span>
                </button>
              </li>
            );
          })}
          {filteredEntries.length === 0 && (
            <li className={styles.empty}>This folder has no {filter ? "matching" : ""} entries.</li>
          )}
        </ul>
      )}

      <div className={styles.footer}>
        <span className={styles.selection} data-testid={`${testIdPrefix}-selection`}>
          {selectedPath || "Nothing selected yet"}
        </span>
        {mode === "pickDirectory" && (
          <button
            type="button"
            className={styles.choose}
            disabled={!currentPath}
            onClick={handleChooseCurrent}
          >
            Choose this folder
          </button>
        )}
      </div>
    </div>
  );
}

function formatSize(sizeBytes: number | null): string {
  if (sizeBytes === null) return "";
  if (sizeBytes < 1024) return `${sizeBytes} B`;
  if (sizeBytes < 1024 * 1024) return `${(sizeBytes / 1024).toFixed(1)} KB`;
  return `${(sizeBytes / (1024 * 1024)).toFixed(1)} MB`;
}