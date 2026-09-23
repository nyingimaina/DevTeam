// Recognizes a short list of discrete options at the END of an assistant message (a numbered,
// lettered, or bulleted list, e.g. "1. double\n2. decimal") and turns it into option labels a
// quick-reply button group can render — saving the user the keystrokes of typing back one of a
// small set of known answers.

const MAX_OPTIONS = 4;
const MIN_OPTIONS = 2;
const MAX_OPTION_LENGTH = 120;

const LIST_ITEM = /^\s*(?:\d+[.)]|[a-zA-Z][.)]|\(?[a-zA-Z]\)|[-*])\s+(.+)$/;

export function parseQuickReplyOptions(bodyText: string | null | undefined): string[] | null {
  if (!bodyText) return null;

  const lines = bodyText.split("\n");
  const trailing: string[] = [];

  // Walk backwards collecting a contiguous run of list-item lines at the very end of the
  // message — a list earlier in the text (e.g. a recap of prior steps) isn't the live question.
  for (let i = lines.length - 1; i >= 0; i--) {
    const line = lines[i];
    if (line.trim().length === 0) {
      if (trailing.length > 0) break; // blank line after we've started collecting ends the run
      continue; // trailing blank lines before the list are fine
    }
    const match = LIST_ITEM.exec(line);
    if (!match) break;
    trailing.unshift(stripLabelMarkup(match[1].trim()));
  }

  if (trailing.length < MIN_OPTIONS || trailing.length > MAX_OPTIONS) return null;
  if (trailing.some((option) => option.length === 0 || option.length > MAX_OPTION_LENGTH)) return null;

  return trailing;
}

function stripLabelMarkup(text: string): string {
  // "**Light** — a bright theme" -> "Light — a bright theme"
  return text.replace(/\*\*(.+?)\*\*/g, "$1");
}
