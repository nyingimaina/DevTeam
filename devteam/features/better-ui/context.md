# Context bundle — better-ui (Remove tool-call noise from the assistant chat bubble)

## Approved requirements (BRS contract)

# better-ui - Business Requirements Specification

| | |
|---|---|
| **Feature** | `better-ui` |
| **Status** | Draft - ready for signoff |
| **Audience** | Any developer who builds this. You should not need to ask anyone anything. |
| **Scope** | Front-end only. The Next.js UI under `front-end/`. The broker (`back-end/`) and the Avalonia shell (`DevTeam.Desktop`) are untouched. |
| **Related** | `HANDOFF.md` (repo state), `NAVIGATION_BRS.md` (project navigation redesign) |

---

## 0. How to use this document

1. Every requirement has a **REQ-N** id and one Given/When/Then acceptance criterion.
2. The build order is: write/rename the failing test (`front-end/app/Chat/UI/MessageRow.test.tsx`) first, then make it pass.
3. Test command: `cmd.exe /d /c npm --prefix front-end test`.

---

## 1. Executive summary & scope

### 1.1 Business objective

While chatting with the agent, the assistant's message bubble is polluted with one
raw tool-call line per tool invocation - e.g. `Tool call #svcnq0q4`, `Tool call
#vp76wv6y`, `Tool call #1jfbutmv`, ... These carry no meaning for the reader and
make the conversation hard to follow. This feature removes that noise so the
bubble shows only the assistant's actual message.

### 1.2 Quantified KPIs

- Assistant bubble shows 0 tool-call labels and 0 tool-call ids across a
  transcript containing any number of tool invocations (was: 1 line per
  invocation).
- Front-end test suite stays green (baseline: 570 passed, 1 skipped, 56 suites).

### 1.3 System context & boundaries

DevTeam is a hybrid application:

- `back-end/DevTeam.Broker` - C# .NET broker, minimal APIs + SignalR.
- `front-end/` - Next.js 13.5.6 (app dir, static export `output: "export"`), Jest + React Testing Library.
- `DevTeam.Desktop` - Avalonia/WebView2 shell that hosts the exported UI.

The noise is rendered by `ToolCallBody` in
`front-end/app/Chat/UI/MessageRow.tsx`, one `<div data-testid="tool-call">` per
`tool_call` entry in the message's `parts`. `MessageRow` is the single place chat
messages are rendered (also used by `app/Project/Release/ReleaseWizard.tsx`), so
changing it fixes every chat surface.

- **In scope:** the chat message bubble renderer (`front-end/app/Chat`).
- **Out of scope (do not build):** the broker/ACP wire format or the message
  `parts` data itself (broker is untouched); the release "feed" side panel; the
  desktop shell; any redesign of the bubble beyond removing the tool-call lines.
- **Existing behaviour preserved:** `RichText.cleanAssistantBody` already strips
  streamed `tool: <name>` / `call_<id>` echo lines from assistant body text - keep
  that working.

### 1.4 Actor matrix

| Actor | Interaction |
|---|---|
| End user | Reads the assistant's replies in the chat bubble; sees only prose, no tool-call noise. |
| Developer | Edits `MessageRow.tsx` + its colocated test; runs `npm --prefix front-end test`. |
| Broker (`DevTeam.Broker`) | Unchanged - still sends `parts` including `tool_call` entries. |

## 2. Agreed decisions (recorded)

| # | Decision | Value |
|---|---|---|
| D1 | Scope boundary | **Front-end only** - the Next.js UI under `front-end/`. Broker API and desktop shell are untouched. |
| D2 | Tool-call noise | **Remove entirely** - the assistant bubble must render no `Tool call #<id>` (or friendly-label) lines at all. |
| D3 | Feature scope | **Whole feature is this one change** - no other UI work is bundled in. |
| D4 | Code location | **In place** - edit `front-end/app/Chat/UI/MessageRow.tsx` + its colocated test; no new feature folder. |
| D5 | Replacement indicator | **None** - tool-call lines are dropped, not collapsed, moved, or summarised. |

## 3. Requirements list

| ID | Title | Priority |
|---|---|---|
| REQ-1 | Assistant chat bubble renders no tool-call lines | MUST |
| REQ-2 | Streamed tool-echo text stays hidden from the bubble | MUST |

## REQ-1: Assistant chat bubble renders no tool-call lines

Given an assistant chat message whose `parts` include one or more `tool_call`
entries (with or without a `toolName`),
When the message is rendered in the conversation,
Then the assistant bubble shows only the assistant's body text and contains no
tool-call label, friendly tool name, or `#<id>` chip (no element with
`data-testid="tool-call"`).

## REQ-2: Streamed tool-echo text stays hidden from the bubble

Given an assistant message whose body text contains streamed tool-echo lines
such as `tool: execute` or `call_abc12345xyz`,
When the message is rendered,
Then those echo lines remain absent from the visible text of the bubble.

---

## 4. Traceability & verification

| ID | Priority | Verification method | Acceptance metric |
|---|---|---|---|
| REQ-1 | MUST | Jest + React Testing Library unit test on `MessageRow` (replace the current "renders a tool call chip..." test with an absence assertion) | Rendering a message with `tool_call` parts yields no `data-testid="tool-call"` nodes and no `Tool call` / `#<id>` text |
| REQ-2 | MUST | Existing `RichText` / `MessageRow` echo-stripping tests | No `tool:` / `call_` line text appears in the rendered bubble |

**Test command:** `cmd.exe /d /c npm --prefix front-end test`

## Allowed working areas

- Code: `front-end/app/Chat`
- Artifacts: `devteam/features/better-ui/*`

## Shared core (REUSE BEFORE WRITING)

The workspace ships one shared core app that every feature extends. Check these locations before writing new code — implement in the core when the logic is shared, and reuse the core's types/components rather than duplicating them.
- `D:/work/nyingi/code/systems/DevTeam/back-end/DevTeam.Shared`

## Working rules
- Implement only what each REQ demands. Do not invent scope.
- Add tests first; the verify/code gate runs them and reports failures.
- Only edit files under the allowed working areas above.

(Generated by DevTeam at 2026-10-01T06:57:01.5605894+00:00.)