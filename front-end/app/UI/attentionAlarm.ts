/** How long a still-unanswered request waits before it sounds again. */
export const REMINDER_MS = 2 * 60 * 1000;

/**
 * Whether the alarm should sound now: immediately for anything newly asking for the person, and
 * again as a reminder while it stays unanswered. Pure so the timing can be tested without audio
 * or timers - a chime that fires on every poll would be worse than none.
 */
export function decideChime(
  previousIds: ReadonlySet<string>,
  currentIds: readonly string[],
  lastChimeAt: number | null,
  now: number,
  reminderMs: number = REMINDER_MS,
): boolean {
  if (currentIds.length === 0) return false;
  if (currentIds.some((id) => !previousIds.has(id))) return true;
  return lastChimeAt !== null && now - lastChimeAt >= reminderMs;
}

/**
 * A short two-note chime from the Web Audio API, so there is no sound file to ship or lose.
 * Best effort by design: a browser may refuse audio before the first click, and a failed chime
 * must never break the screen it is announcing (the banner and the desktop toast still show).
 */
export function playAttentionChime(): void {
  try {
    const AudioCtor: typeof AudioContext | undefined =
      typeof window === "undefined"
        ? undefined
        : window.AudioContext ?? (window as unknown as { webkitAudioContext?: typeof AudioContext }).webkitAudioContext;
    if (!AudioCtor) return;

    const context = new AudioCtor();
    void context.resume?.();
    const start = context.currentTime;
    const notes: [number, number][] = [[880, 0], [660, 0.22]];
    for (const [frequency, offset] of notes) {
      const oscillator = context.createOscillator();
      const gain = context.createGain();
      oscillator.type = "sine";
      oscillator.frequency.value = frequency;
      gain.gain.setValueAtTime(0.0001, start + offset);
      gain.gain.exponentialRampToValueAtTime(0.25, start + offset + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.0001, start + offset + 0.35);
      oscillator.connect(gain).connect(context.destination);
      oscillator.start(start + offset);
      oscillator.stop(start + offset + 0.4);
    }
    window.setTimeout(() => void context.close?.(), 1000);
  } catch {
    // Audio is a nicety on top of the banner, never a requirement.
  }
}
