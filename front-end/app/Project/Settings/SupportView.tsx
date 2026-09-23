"use client";
import React, { useCallback, useEffect, useState } from "react";
import BrokerApi from "../../Chat/Data/BrokerApi";
import { DiagnosticsSettingsDto, NotificationSettingsDto } from "../../Chat/Data/BrokerTypes";
import { clearErrors, recentErrors } from "../../UI/diagnostics";
import { saveBlob } from "../../UI/download";
import { toErrorMessage } from "../Release/ReleaseWizard";
import styles from "../Styles/Support.module.css";

interface ISupportViewProps {
  api: BrokerApi;
}

// Everything a non-technical person needs to hand a problem to someone technical: turn on
// detail, repeat the problem, collect one file, send it. No logs to find, no jargon to read.
export default function SupportView({ api }: ISupportViewProps) {
  const [settings, setSettings] = useState<DiagnosticsSettingsDto | null>(null);
  const [notifications, setNotifications] = useState<NotificationSettingsDto | null>(null);
  const [problemCount, setProblemCount] = useState(0);
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [collecting, setCollecting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const loaded = await api.getDiagnosticsSettingsAsync();
        if (!cancelled) setSettings(loaded);
        const notifications = await api.getNotificationSettingsAsync();
        if (!cancelled) setNotifications(notifications);
      } catch (e) {
        if (!cancelled) setError(toErrorMessage(e));
      }
      if (!cancelled) setProblemCount(recentErrors().length);
    })();

    return () => {
      cancelled = true;
    };
  }, [api]);

  const toggleVerbose = useCallback(async (enabled: boolean) => {
    setSaving(true);
    setError(null);
    setNotice(null);
    try {
      setSettings(await api.setVerboseLoggingAsync(enabled));
      setNotice(enabled ? "Detailed logging is on. Repeat the problem, then collect the file." : "Detailed logging is off.");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setSaving(false);
    }
  }, [api]);

  const collect = useCallback(async () => {
    setCollecting(true);
    setError(null);
    setNotice(null);
    try {
      const report = await api.collectDiagnosticsAsync();
      saveBlob(report, `devteam-diagnostics-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.zip`);
      setNotice("Saved. Send that file to whoever is helping you.");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setCollecting(false);
    }
  }, [api]);

  const clearRecorded = useCallback(() => {
    clearErrors();
    setProblemCount(0);
  }, []);

  const toggleNotification = useCallback(async (key: keyof NotificationSettingsDto, enabled: boolean) => {
    setError(null);
    try {
      setNotifications(await api.setNotificationSettingsAsync({ [key]: enabled }));
    } catch (e) {
      setError(toErrorMessage(e));
    }
  }, [api]);

  const sendTestNotification = useCallback(async () => {
    setTesting(true);
    setError(null);
    setNotice(null);
    try {
      await api.sendTestNotificationAsync();
      setNotice("Sent — you should see it appear on your desktop.");
    } catch (e) {
      setError(toErrorMessage(e));
    } finally {
      setTesting(false);
    }
  }, [api]);

  return (
    <div className={styles.view} data-testid="support-view">
      <h2 className={styles.title}>Support</h2>
      <p className={styles.intro}>
        If something is going wrong, turn on detailed logging, repeat what you did, then collect a
        file and send it to whoever is helping you. The file has the recent activity and a report
        of what failed — it is safe to share.
      </p>

      {error && (
        <div className={styles.error} data-testid="support-error">
          {error}
        </div>
      )}
      {notice && (
        <div className={styles.notice} data-testid="support-notice">
          {notice}
        </div>
      )}

      <section className={styles.section}>
        <label className={styles.toggle}>
          <input
            type="checkbox"
            checked={settings?.verboseLogging ?? false}
            disabled={saving || settings === null}
            onChange={(e) => void toggleVerbose(e.target.checked)}
            data-testid="support-verbose-toggle"
          />
          <span>
            Detailed logging
            <span className={styles.hint}>
              Keeps a much more complete record of what the app is doing. Leave it on while you
              reproduce the problem.
            </span>
          </span>
        </label>
      </section>

      <section className={styles.section}>
        <div className={styles.actions}>
          <button
            type="button"
            className={styles.primary}
            onClick={() => void collect()}
            disabled={collecting}
            data-testid="support-collect-btn"
          >
            {collecting ? "Collecting…" : "Collect diagnostics file"}
          </button>
          <button
            type="button"
            className={styles.secondary}
            onClick={() => void api.revealLogsAsync()}
            data-testid="support-open-logs-btn"
          >
            Open the logs folder
          </button>
        </div>
        {settings && (
          <div className={styles.path} data-testid="support-logs-path">
            Logs are kept in {settings.logsDirectory}
          </div>
        )}
      </section>

      <section className={styles.section}>
        <div className={styles.recorded} data-testid="support-recorded">
          {problemCount === 0
            ? "No problems recorded in this session."
            : `${problemCount} recent problem${problemCount === 1 ? "" : "s"} recorded — they will be included in the file.`}
        </div>
        {problemCount > 0 && (
          <button
            type="button"
            className={styles.secondary}
            onClick={clearRecorded}
            data-testid="support-clear-btn"
          >
            Clear the list
          </button>
        )}
      </section>

      <section className={styles.section} data-testid="support-notifications">
        <h3 className={styles.subheading}>Desktop notifications</h3>
        <p className={styles.hint}>
          DevTeam has no window of its own, so these tell you when something happened while you
          were looking elsewhere.
        </p>
        {notifications && (
          <div className={styles.notificationList}>
            {NOTIFICATION_TOGGLES.map((toggle) => (
              <label key={toggle.key} className={styles.toggle}>
                <input
                  type="checkbox"
                  checked={notifications[toggle.key]}
                  onChange={(e) => void toggleNotification(toggle.key, e.target.checked)}
                  data-testid={`support-notify-${toggle.key}`}
                />
                <span>{toggle.label}</span>
              </label>
            ))}
          </div>
        )}
        <button
          type="button"
          className={styles.secondary}
          onClick={() => void sendTestNotification()}
          disabled={testing}
          data-testid="support-test-notification-btn"
        >
          {testing ? "Sending…" : "Send a test notification"}
        </button>
      </section>
    </div>
  );
}

const NOTIFICATION_TOGGLES: { key: keyof NotificationSettingsDto; label: string }[] = [
  { key: "stageComplete", label: "When a step finishes" },
  { key: "needsAttention", label: "When something needs attention" },
  { key: "approvalNeeded", label: "When your approval is needed" },
  { key: "sound", label: "Play a sound" },
];
