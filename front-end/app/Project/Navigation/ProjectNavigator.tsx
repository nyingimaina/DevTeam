import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { ReleaseDto } from "../../Chat/Data/BrokerTypes";
import { usePoller } from "../../Chat/Data/usePoller";
import { toErrorMessage } from "../Release/ReleaseWizard";
import { projectNameFromPath } from "../workspaceCleanup";
import { useHashRoute } from "./useHashRoute";
import { crumbsFor, Route } from "./routes";
import { findActiveFeature } from "./releaseView";
import Breadcrumb from "./Breadcrumb";
import ProjectHome from "./ProjectHome";
import ReleaseFolder from "./ReleaseFolder";
import FeatureView from "./FeatureView";
import styles from "../Styles/Navigation.module.css";

interface IProjectNavigatorProps {
  api: BrokerApi;
  workspacePath: string;
  /** False while another tab (Git/Settings) is shown — the address is then never written. */
  active?: boolean;
}

function upsert(list: ReleaseDto[], fresh: ReleaseDto): ReleaseDto[] {
  return list.some((r) => r.id === fresh.id)
    ? list.map((r) => (r.id === fresh.id ? fresh : r))
    : [...list, fresh];
}

const LIST_POLL_START_MS = 10000;
const LIST_POLL_MAX_MS = 60000;

/**
 * The project → release → feature shell. The whole navigation state is the address bar: each
 * level reads the hash, polls only what it shows, and hands the current release down to the
 * existing screens. Looking changes nothing; only the stage screens' own actions do.
 */
export default function ProjectNavigator({ api, workspacePath, active = true }: IProjectNavigatorProps) {
  const [route, navigate] = useHashRoute(active);
  const [releases, setReleases] = useState<ReleaseDto[]>([]);
  const [pipelineStageNames, setPipelineStageNames] = useState<string[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [releaseNotFound, setReleaseNotFound] = useState(false);

  // Level 0/1 poll the two lists gently; a feature view polls its own release instead (§6.4),
  // so only one level ever polls at a time.
  const atListLevel = route.kind === "home" || route.kind === "release";
  const { pollNow } = usePoller<{ releases: ReleaseDto[]; hotfixes: ReleaseDto[] }>({
    enabled: active && atListLevel,
    func: async () => {
      const [releases, hotfixes] = await Promise.all([
        api.listReleasesAsync(workspacePath),
        api.listHotfixesAsync(workspacePath),
      ]);
      return { releases: releases ?? [], hotfixes: hotfixes ?? [] };
    },
    onResult: ({ releases: r, hotfixes: h }) => {
      setReleases([...r, ...h]);
      setError(null);
      setLoading(false);
    },
    onError: (e) => {
      setError(toErrorMessage(e));
      setLoading(false);
    },
    pollIntervalMilliseconds: LIST_POLL_START_MS,
    maxIntervalMilliseconds: LIST_POLL_MAX_MS,
    deps: [workspacePath],
  });

  const addOrReplaceRelease = useCallback((fresh: ReleaseDto) => {
    setReleases((prev) => upsert(prev, fresh));
  }, []);

  // "Home" has no releaseId; cut the union down so the effect below is easy to read and to depend on.
  const routedReleaseId = route.kind === "home" ? null : route.releaseId;

  // A release that isn't in the lists yet (deep link, or just created) is fetched once so a
  // refresh or a pasted address still lands in the right place. Derived each render; never kept.
  useEffect(() => {
    if (!routedReleaseId) {
      setReleaseNotFound(false);
      return;
    }
    if (releases.some((r) => r.id === routedReleaseId)) {
      setReleaseNotFound(false);
      return;
    }
    let cancelled = false;
    setLoading(false);
    api.getReleaseAsync(routedReleaseId)
      .then((r) => {
        if (r && !cancelled) addOrReplaceRelease(r);
      })
      .catch(() => {
        if (!cancelled) setReleaseNotFound(true);
      });
    return () => { cancelled = true; };
  }, [api, routedReleaseId, releases, addOrReplaceRelease]);

  // The pipeline is the same for every feature of a project; fetch its stage names once.
  useEffect(() => {
    if (pipelineStageNames.length > 0) return;
    const firstFeature = releases.find((r) => r.features.length > 0)?.features[0];
    if (!firstFeature) return;
    let cancelled = false;
    api.getPipelineAsync(firstFeature.id)
      .then((p) => { if (!cancelled) setPipelineStageNames((p ?? []).map((s) => s.name)); })
      .catch(() => undefined);
    return () => { cancelled = true; };
  }, [api, releases, pipelineStageNames.length]);

  const current = route.kind === "home" ? undefined : releases.find((r) => r.id === route.releaseId);
  const activeFeature = findActiveFeature(releases);
  const activeFeatureKey = activeFeature ? { key: activeFeature.feature.key } : null;

  const notFound = route.kind !== "home" && releaseNotFound && !current;

  const releaseCrumbLabel = current
    ? current.title ?? current.features[0]?.key ?? "Release"
    : "Release";
  const featureCrumbLabel =
    route.kind === "feature" || route.kind === "stage"
      ? current?.features.find((f) => f.id === route.featureId)?.key ?? "Feature"
      : "Feature";
  const names = {
    project: projectNameFromPath(workspacePath),
    release: releaseCrumbLabel,
    feature: featureCrumbLabel,
  };

  const renderFeature = (featureId: string) => {
    if (!current) return null;
    return (
      <>
        <Breadcrumb crumbs={crumbsFor({ kind: "feature", releaseId: current.id, featureId }, names)} />
        <FeatureView
          api={api}
          release={current}
          featureId={featureId}
          onReleaseUpdated={addOrReplaceRelease}
        />
      </>
    );
  };

  return (
    <div data-testid="project-navigator">
      {route.kind === "home" && (
        <>
          <Breadcrumb crumbs={crumbsFor({ kind: "home" }, names)} />
          <ProjectHome
            api={api}
            workspacePath={workspacePath}
            releases={releases}
            loading={loading}
            error={error}
            onRetry={pollNow}
            onReleaseCreated={(fresh) => {
              addOrReplaceRelease(fresh);
              navigate({ kind: "release", releaseId: fresh.id });
            }}
          />
        </>
      )}

      {notFound && (
        <div className={styles.notFound} data-testid="project-not-found">
          <p>We couldn&apos;t find that release in this project.</p>
          <a href="#/">Back to the project</a>
        </div>
      )}

      {route.kind === "release" && current && !notFound && (
        // An urgent-fix folder holds exactly one feature, so opening it lands straight on the
        // workflow; the breadcrumb still shows the folder.
        current.isHotfix && current.features.length === 1
          ? renderFeature(current.features[0].id)
          : (
            <>
              <Breadcrumb crumbs={crumbsFor({ kind: "release", releaseId: current.id }, names)} />
              <ReleaseFolder
                api={api}
                release={current}
                pipelineStageNames={pipelineStageNames}
                activeFeature={activeFeatureKey}
                onReleaseUpdated={addOrReplaceRelease}
                onFeatureCreated={(featureId) => navigate({ kind: "feature", releaseId: current.id, featureId })}
              />
            </>
          )
      )}

      {(route.kind === "feature" || route.kind === "stage") && current && !notFound && renderFeature(route.featureId)}
    </div>
  );
}