import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { NegotiationPointDto } from "../../Chat/Data/BrokerTypes";
import styles from "../Styles/Negotiation.module.css";
import { negotiationHeading, pointResponseLabel, pointStatusLabel } from "./negotiation";

interface INegotiationPanelProps {
  featureId: string;
  api: BrokerApi;
  testIdPrefix: string;
  refreshRelease: () => Promise<unknown> | unknown;
}

/**
 * The visible half of the push-back protocol: which numbered points a stage was sent back with,
 * how it answered each one, and let-the-user-settle-it controls. Hidden entirely when there is
 * nothing to show, so a feature that never bounces looks exactly as before.
 */
export default function NegotiationPanel({ featureId, api, testIdPrefix, refreshRelease }: INegotiationPanelProps) {
  const [points, setPoints] = useState<NegotiationPointDto[]>([]);
  const [busyId, setBusyId] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const result = await api.getNegotiationAsync(featureId);
      setPoints(result ?? []);
    } catch {
      setPoints([]);
    }
  }, [api, featureId]);

  useEffect(() => {
    void load();
  }, [load]);

  const act = useCallback(
    async (id: string, action: (feature: string, finding: string) => Promise<unknown>) => {
      setBusyId(id);
      try {
        await action(featureId, id);
        await load();
        await refreshRelease();
      } finally {
        setBusyId(null);
      }
    },
    [featureId, load, refreshRelease],
  );

  if (points.length === 0) {
    return null;
  }

  return (
    <section className={styles.panel} data-testid={`${testIdPrefix}-negotiation`}>
      <h3 className={styles.heading}>{negotiationHeading(points)}</h3>
      <ol className={styles.list}>
        {points.map((point, index) => (
          <li
            key={point.id}
            className={styles.point}
            data-testid={`${testIdPrefix}-negotiation-point-${point.id}`}
          >
            <div className={styles.summary}>
              <span className={styles.number}>{index + 1}.</span>
              {point.requirementRef && <span className={styles.requirement}>{point.requirementRef}</span>}
              <span>{point.summary}</span>
            </div>
            {point.expected && <div className={styles.expected}>Expected: {point.expected}</div>}
            <div className={styles.meta} data-testid={`${testIdPrefix}-negotiation-status-${point.id}`}>
              {pointStatusLabel(point.status)} · {pointResponseLabel(point.responseKind)}
            </div>
            {point.responseText && <div className={styles.response}>{point.responseText}</div>}
            {point.status === "Open" && (
              <div className={styles.actions}>
                <button
                  type="button"
                  disabled={busyId === point.id}
                  data-testid={`${testIdPrefix}-negotiation-resolve-${point.id}`}
                  onClick={() => void act(point.id, (feature, finding) => api.resolveNegotiationPointAsync(feature, finding))}
                >
                  Mark done
                </button>
                <button
                  type="button"
                  disabled={busyId === point.id}
                  data-testid={`${testIdPrefix}-negotiation-escalate-${point.id}`}
                  onClick={() => void act(point.id, (feature, finding) => api.escalateNegotiationPointAsync(feature, finding))}
                >
                  Escalate
                </button>
              </div>
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}
