import React, { useCallback, useEffect, useRef, useState } from "react";
import BrokerApi from "../Chat/Data/BrokerApi";
import { AttentionItemDto, PendingRulingDto } from "../Chat/Data/BrokerTypes";
import { usePoller } from "../Chat/Data/usePoller";
import { decideChime, playAttentionChime } from "./attentionAlarm";
import styles from "./AttentionBar.module.css";

const POLL_MS = 4000;
const BASE_TITLE_FALLBACK = "DevTeam";

interface IAttentionBarProps {
  api: BrokerApi;
  workspacePath: string;
  /** Take the person to the thing that is waiting. */
  onOpen: (item: AttentionItemDto) => void;
}

/**
 * The one place that says "the team is waiting on you". Shown above every screen, so it cannot
 * be scrolled away from, and deliberately has no dismiss button: it goes away only when the thing
 * it asks for is done. It blocks the work, not the screen - the person still needs to read the
 * pages to make the call - so the way through is its button, which goes to the right place.
 * A challenged test is asked right here, as a question with two plain answers, because sending a
 * novice off to instruct an agent in writing is where these stalls used to end.
 * Alongside it the window title is marked, a sound plays (unless switched off), and the broker
 * raises a desktop notification, so someone who has switched to another window still finds out.
 */
export default function AttentionBar({ api, workspacePath, onOpen }: IAttentionBarProps) {
  const [items, setItems] = useState<AttentionItemDto[]>([]);
  const [answering, setAnswering] = useState<string | null>(null);
  const [continuing, setContinuing] = useState(false);
  // Questions answered this visit, so a quick second answer is not blocked by a stale list.
  const [answered, setAnswered] = useState<Set<string>>(new Set());
  const [problem, setProblem] = useState<string | null>(null);
  const knownIds = useRef<Set<string>>(new Set());
  const lastChimeAt = useRef<number | null>(null);
  const soundOn = useRef(true);

  const { pollNow } = usePoller<{ items: AttentionItemDto[]; sound: boolean }>({
    enabled: true,
    func: async () => {
      const [attention, settings] = await Promise.all([
        api.getAttentionAsync(workspacePath),
        // A settings hiccup must not hide a request for the person - default to sounding.
        Promise.resolve(api.getNotificationSettingsAsync()).catch(() => null),
      ]);
      return { items: attention ?? [], sound: settings?.sound ?? true };
    },
    onResult: ({ items: current, sound }) => {
      soundOn.current = sound;
      const now = Date.now();
      const ids = current.map((item) => item.id);
      if (decideChime(knownIds.current, ids, lastChimeAt.current, now)) {
        lastChimeAt.current = now;
        if (soundOn.current) playAttentionChime();
      }
      knownIds.current = new Set(ids);
      if (ids.length === 0) lastChimeAt.current = null;
      setItems(current);
    },
    pollIntervalMilliseconds: POLL_MS,
    deps: [api, workspacePath],
  });

  // Mark the window so a person on another tab or window sees there is something waiting.
  useEffect(() => {
    if (items.length === 0 || typeof document === "undefined") return;
    const original = document.title || BASE_TITLE_FALLBACK;
    document.title = `(${items.length}) Waiting for you — ${original}`;
    return () => {
      document.title = original;
    };
  }, [items.length]);

  const open = useCallback(() => {
    if (items[0]) onOpen(items[0]);
  }, [items, onOpen]);

  const answer = useCallback(
    async (item: AttentionItemDto, ruling: PendingRulingDto, decision: "accept" | "reject") => {
      setAnswering(ruling.test);
      setProblem(null);
      try {
        await api.recordRulingAsync(item.featureId, ruling.test, decision);
      } catch {
        setProblem("We could not save your answer. Please try again.");
        setAnswering(null);
        return;
      }
      setAnswering(null);
      const done = new Set(answered).add(`${item.id}|${ruling.test}`);
      setAnswered(done);

      // The last question answered: the work was waiting only on this, so carry on.
      const stillOpen = (item.rulings ?? []).filter((r) => !done.has(`${item.id}|${r.test}`));
      if (stillOpen.length === 0) {
        setContinuing(true);
        try {
          await api.runStageAsync(item.featureId);
        } catch {
          setProblem("Your answer is saved, but the work could not be restarted. Use the button to open it and try again.");
        } finally {
          setContinuing(false);
        }
      }
      pollNow();
    },
    [api, pollNow, answered],
  );

  if (items.length === 0) return null;

  const first = items[0];
  const others = items.length - 1;
  const rulings = (first.rulings ?? []).filter((r) => !answered.has(`${first.id}|${r.test}`));
  const actionLabel = first.kind.toLowerCase() === "approval" ? "Review and approve" : "See what's needed";

  return (
    <section
      className={styles.bar}
      role="alert"
      aria-live="assertive"
      aria-label="Waiting for you"
      data-testid="attention-bar"
    >
      <div className={styles.row}>
        <div className={styles.text}>
          <strong className={styles.title}>{first.title}</strong>
          <span className={styles.message}>{first.message}</span>
          {others > 0 && (
            <span className={styles.more}>
              {others === 1 ? "1 more thing is" : `${others} more things are`} also waiting.
            </span>
          )}
        </div>
        <button type="button" className={styles.action} onClick={open}>
          {actionLabel}
        </button>
      </div>

      {rulings.map((ruling) => (
        <div key={ruling.test} className={styles.question} data-testid="ruling-question">
          <div className={styles.questionTitle}>{ruling.test}</div>
          {ruling.expected && (
            <div>
              <span className={styles.label}>The requirement says:</span> {ruling.expected}
            </div>
          )}
          {ruling.observed && (
            <div>
              <span className={styles.label}>What the checker found:</span> {ruling.observed}
            </div>
          )}
          {ruling.details && (
            <details className={styles.details}>
              <summary>More detail</summary>
              <pre>{ruling.details}</pre>
            </details>
          )}
          <div className={styles.answers}>
            <button
              type="button"
              className={styles.answer}
              disabled={answering !== null || continuing}
              onClick={() => void answer(first, ruling, "accept")}
            >
              The test is wrong — update the requirement
            </button>
            <button
              type="button"
              className={styles.answer}
              disabled={answering !== null || continuing}
              onClick={() => void answer(first, ruling, "reject")}
            >
              The requirement is right — fix the code
            </button>
          </div>
        </div>
      ))}

      {continuing && <div className={styles.status}>Thanks — getting back to work…</div>}
      {problem && <div className={styles.problem}>{problem}</div>}
    </section>
  );
}
