# DevTeam — Handoff / Status

Last updated: 2026-09-13 (stream casing / side-pane / theme / UI polish; repo clean after commit).

## CRITICAL SAFETY RULE (why momentum kept breaking)
- NEVER run `Stop-Process -Name opencode` or `taskkill /IM opencode.exe`.
  The opencode CLI that hosts this session IS an `opencode.exe`; killing by
  name kills the harness and interrupts all work.
- Kill broker processes only by their exact PID (read it from a pid file):
  `Stop-Process -Id <pid> -Force`. The broker spawns its own opencode child;
  stopping `DevTeam.Broker` by exact PID is safe.
- The bash tool sometimes reports `ChildProcess.kill` for `Start-Process`
  with `-RedirectStandard{Output,Error}`; retry once on that error. Avoid
  combining `Stop-Process` of an older broker in the same command that starts a
  new one (keeps the harness watchdog happy).

## Repo state (git)
- `ab86bd7` root commit: initial implementation (broker + Next.js UI + tests).
- `19f476f` "stuff": DI fix (registered `IAcpProcess` -> `OpencodeAcpProcess`),
  `RuntimeIdentity.OpenCodePath`, lazy `initialize` handshake in
  `BrokerCoordinator.EnsureInitializedAsync`, scripted-test updates.
- (newest) mode-picker commit: `selectwrapper-mode-picker` - mode support
  (see "Mode feature" below) + this HANDOFF. Working tree CLEAN at newest.
- (2026-09-13) bug-fix/polish commit: `big-pickle` run — stream payload
  camelCase fix, side-pane render fix, app-wide theme (light/dark/system),
  semantic button types, Zest textbox progress bars, playful reduced-motion
  animations (see "Bugs fixed + UI polish" below).
- Test counts: backend 53/53 green (non-opencode-integration; full suite runs 2
  real-opencode tests, >10 min); frontend 37/37 green (`next build` + `next
  lint` clean; full `build.ps1` publish OK + smoke-tested vs real opencode).

## Architecture recap
- `back-end/DevTeam.Broker` (C# .NET 10, ASP.NET Core minimal APIs + SignalR):
  `Program.cs` (host, DI: RuntimeIdentity, IAcpProcess->OpencodeAcpProcess,
  IAgentSpoke->OpencodeAcpSpoke, BrokerCoordinator, IEventBroadcaster,
  DbContextFactory<DevTeamDbContext> SQLite, /hub, SPA fallback),
  `ApiEndpoints.cs`, `Server/Dtos.cs`, `Server/BrokerCoordinator.cs`,
  `Server/BrokerHub.cs` + `IEventBroadcaster.cs`, `Spoke/*` (opencode ACP
  spoke), `Rpc/*` (newline-delimited JSON-RPC over stdio), `Domain/*` (EF Core).
- `front-end/` (Next.js 13.5.6 app-dir, static export `output:"export"`,
  jest+RTL, jattac zest-button/zest-textbox/zest-responsive-layout,
  module-state-manager). Chat feature: `app/Chat/{Data,State,UI,Styles}`.
  37 frontend tests green (includes Theme + ThemePicker + Chat smoke).
- `build.ps1`: npm ci -> next build -> copy front-end/out -> broker wwwroot ->
  dotnet publish win-x64 -> publish/broker. `-SkipFrontend` rebuilds broker only.

## ACP wire facts (opencode 1.18.14, verified)
- Methods: `initialize {protocolVersion:1, clientCapabilities:{}}`,
  `session/new {cwd, mcpServers:[]}`, `session/prompt {sessionId, prompt:[{type:"text",text}]}`,
  `session/set_model {sessionId, modelId}`, `session/set_config_option`,
  `session/set_mode {sessionId, modeId}` (spec-documented client way to change mode),
  `session/cancel` (notification), `session/request_permission` (server->client).
- Streaming: `session/update` notifications with `sessionUpdate` kinds:
  `agent_message_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`,
  `usage_update`, `config_option_update`, `available_commands_update`,
  and (spec) `current_mode_update`.
- `session/new` result: `{sessionId, configOptions:[...], models, modes?, _meta}`.
  `configOptions` includes `id:"model"` and, when modes exist, `id:"mode"`
  (type "select", `currentValue` = current mode id, `options`=[{value,name,description?}]).
- IMPORTANT: broker MUST send `initialize` before ANY other method; that is now
  guaranteed by `BrokerCoordinator.EnsureInitializedAsync` (lazy, once).
- Modes come from opencode agent definitions; empty list if none configured.
  UI must render gracefully when no modes are available.

## Handler of "hang" resolved (root cause)
- Broker never called `initialize`; `session/new`->`session/prompt` hung waiting
  on a reply opencode would not give before handshake. Fixed by lazy init in
  `BrokerCoordinator` (init lock + `_initialized` flag) invoked from
  `NewSessionAsync` and `PromptAsync`. Scripted BrokerCoordinatorTests updated
  to drive the `initialize` handshake first (`InitializeResultJson` + helper in
  `CreateSessionAsync`); `Prompt_OnUnknownSession_Throws` updated likewise.

## Smoke-test checklist (run after any broker change)
1. `powershell -ExecutionPolicy Bypass -File build.ps1 -SkipFrontend`
2. Start: `Start-Process publish/broker/DevTeam.Broker.exe -WindowStyle Hidden`,
   save PID to `$env:TEMP\opencode\devteam-broker.pid`; poll `/healthz` (200 ok).
3. `POST /api/sessions {workspacePath}` -> 201 (spawns opencode, session id,
   includes `modes` + `modeId`).
4. `POST /api/sessions/{id}/mode {modeId}` -> ok; GET shows persisted modeId.
5. `POST /api/sessions/{id}/prompt {text}` -> completes (stopReason, tokens).
6. Verify assistant message persisted via `GET /api/sessions/{id}` (2 messages).
7. Kill broker by PID only. NEVER by opencode name.

## Mode feature (COMMITTED) — "SelectWrapper to pick the mode"
Full-stack and smoke-tested against real opencode:
- Backend: `session/set_mode` on `OpencodeAcpSpoke` (`IAgentSpoke.SetModeAsync`),
  `BrokerCoordinator.SetModeAsync` (persists `ModeId` on `DevTeamSession`),
  `POST /api/sessions/{id}/mode {modeId}`, `SetModeRequest` DTO.
- Mode surfaced in DTOs: `SessionSummary`/`SessionDetail` gain `ModeId` + `Modes`
  (from the `mode` config option in `session/new`, mirroring the `model` option;
  note `modes` is actually the ACP `availableModes`, which arrives via
  configOptions id "mode"). `NewSessionAsync` persists the acp currentValue.
- DB: `ModeId` column added. EnsureCreated does NOT migrate an existing SQLite
  DB - if you get "no such column: s.ModeId", DELETE `~\.devteam\devteam.db`
  (throwaway dev data) before restarting the broker.
- Frontend: new reusable `SelectWrapper` (`app/Chat/UI/SelectWrapper.tsx` +
  `Styles/SelectWrapper.module.css`, generic `SelectOption` prop, placeholder,
  disabled, aria-label) with 4 Jest tests. `ModelSidePane` gains `modes`,
  `currentModeId`, `onSelectMode` and renders the mode `<select>` (data-testid
  `mode-select`; "No modes available." when empty, matching the model pattern).
  `ChatLogic.switchModeAsync` + `BrokerApi.setModeAsync`; `currentModeId` flows
  through initialize/create/switch/delete. Chat.tsx wires it (does NOT close the
  pane on mode change).
- Verified smoke (published broker + real opencode): `session/new` -> `modes`
  [{build},{plan}], `modeId:"build"`; `POST /mode {"modeId":"plan"}` ok and
  persisted; prompt -> `end_turn` (lazy-init fix confirmed end-to-end);
  user+assistant messages persisted.
- NOTE: on a real agent, `modes` is the opencode agent list (build/plan here);
  empty when no agents configured - UI degrades gracefully.

## Bugs fixed + UI polish (2026-09-13, committed)
- **Bug 1 - "undefined" while streaming:** `BrokerCoordinator.FireAsync`
  serialized StreamEvent payloads with default (PascalCase) options, so the UI
  saw `{MessageId,Text,...}` but reads camelCase (`p.messageId`,`p.text`) ->
  "undefined..." until turnEnd reloaded real text. Fixed: payloads now serialize
  with `new JsonSerializerOptions(JsonSerializerDefaults.Web)` (static
  `PayloadJsonOptions`); `BrokerCoordinatorTests` assert `messageId`/`text`
  (regression lock). Wire JSON is now uniformly camelCase (REST + StreamEvent
  payloads).
- **Bug 2 - side pane never rendered (list button dead):** `ZestResponsiveLayout`
  is the component that actually renders the side-pane stack from
  `SidePaneProvider` context (confirmed in the lib bundle: no `sidePane` prop +
  depth 0 => maps the stack to `SidePane`s). Chat only wrapped
  `SidePaneProvider`, so `openSidePane(...)` was a silent no-op. Fixed: Chat
  renders `<ZestResponsiveLayout><ChatInner/></ZestResponsiveLayout>` inside the
  provider; workspace-picker pane now closes after a session is created.
- **App-wide theme (light/dark/system):** `ThemeProvider` now exposes
  `{mode, resolvedTheme, setMode}` ('system' default, persisted to
  localStorage `devteam.theme`, live `matchMedia("(prefers-color-scheme: dark)")`
  listener, applies `document.documentElement.dataset.theme` via useLayoutEffect).
  `layout.tsx` drops hardcoded `data-theme="light"` for a tiny inline pre-paint
  script (no flash, no hydration mismatch). `globals.css` gains
  `html[data-theme="dark"]` overrides + tokens (`--danger`, `--danger-bg`,
  `--surface-hover{,-strong}`, `--accent-soft`, `--accent-ring`,
  `--shadow-overlay`) replacing hardcoded colors. Zest defaults providers already
  feed `resolvedTheme` so all Zest controls follow the app theme automatically.
  `ThemePicker` (Light/System/Dark segmented) lives in `ModelSidePane` (Theme
  section), wired via `useTheme` in Chat.
- **Semantic button types:** every native `<button>` (and the ZestButtons) now
  sets an explicit `type` (`button` everywhere — this app has no `<form>`, so
  nothing is a submit). Zest `semanticType` defaults were avoided on purpose:
  they drag in icons/confirm dialogs/success checkmarks; explicit `type` gives
  the semantic guarantee without chrome.
- **Textbox progress bars (config inputs):** `ZestTextboxDefaultsProvider` now
  defaults `showProgressBar: true` + `animatedCounter: true`, so any ZestTextbox
  with a `maxLength` shows the counter/progress bar. The workspace-path input in
  `WorkspacePicker` was converted from a native `<input>` to `ZestTextbox`
  (`maxLength={260}`). Chat composer untouched.
- **Playful-but-corporate animations:** Zest side-pane bounce kept (the playful
  moment); messages fade/slide in (`messageIn`); html/body cross-fade on theme
  change; subtle hover micro-interactions (dock button, + New conversation,
  row transitions). Everything motion-y is inside
  `@media (prefers-reduced-motion: no-preference)`; the `reduce` branch disables
  it.
- Frontend tests for all of the above: ThemeProvider (defaults, persistence,
  OS-follow, listener), ThemePicker, ModelSidePane theme + button types, and a
  Chat smoke test (mocked ChatLogic) proving the list button opens the pane,
  theme switching applies `data-theme`, and the workspace picker input is capped
  at 260.
- Smoke (published broker + real opencode): session create (modes+build),
  `POST /mode plan` (persisted), prompt -> `end_turn` (12216 tokens), reply
  "SMOKE-OK" persisted (user+assistant). Browser-only checks (dark rendering,
  animations, progress bar visuals) need a manual run.

## Ship-readiness gate + Checks library (UNCOMMITTED, 2026-09-21)
Replaces `verify.ps1` with a first-class, enforced gate. Two hard gates, both must pass:
- **Per feature** — a new `verification` pipeline stage (after `qa`). It is deterministic:
  no Agent step, no Signoff, no opencode session. A feature only reaches Complete (and merges
  into its release) once `final_checks` passes. `WorkflowEngine.RoleUsesAgent` is what lets an
  agent-less stage run without opening a session.
- **Per release** — `IShipReadinessGate.CheckReleaseAsync` runs before
  `POST /api/releases/{id}/finalize` merges into a protected branch, and records a
  **sha-pinned** `ReadinessAttestation`. `POST /api/git/merge` into `main`/`master` is refused
  (409) unless a passing attestation exists AND the branch still points at the attested commit
  (see `ProtectedBranches`, `ShipReadinessGate.HasValidAttestationAsync`). Only `main`/`master`
  are protected; `develop` is not.

Backend pieces:
- `Gates/Readiness/`: `ReadinessProfileLoader` (auto-detects `*.slnx`/`*.sln` + `package.json`,
  or a hand-authored `devteam/readiness.yaml`), `ReadinessChecker` (runs phases via
  `IProcessRunner`, **through the platform shell** so npm/npx .cmd shims work; per-phase
  timeouts, coverage floors, skipped=pass), `ReadinessMetricsParser` (TRX/jest/cobertura),
  `CheckCatalog` (plain-language copy per phase), `ReadinessEnvironment` (DB probe).
- `Gates/FinalChecksGate.cs`, `Workflow/FinalChecksGate` registration in `BuiltinGateRegistry`
  (Create now takes `IReadinessChecker`).
- `Workflow/ShipReadinessGate.cs` (+ `ReadinessReportView` / `ProtectedBranches`).
- `Domain/ReadinessEntities.cs`: `ReadinessReportRow`, `ReadinessCheckRow`,
  `ReadinessAttestation` (raw output + `MetricsJson` are persisted so the report is rendered
  in-app — no stored HTML). Auto-created by `DevTeamDbContextSchemaSync`.
- `GitCommandHandler` gains `rev-parse` (→ `IGitService.HeadCommitAsync`) for the sha pin.
- Endpoints: `POST/GET /api/releases/{id}/readiness`, `GET .../readiness/history`,
  `GET /api/checks?workspacePath=`.

Frontend:
- New **Checks** tab (`app/Project/Checks/ChecksView.tsx`, `charts.ts`, `Styles/Checks.module.css`)
  using **recharts** (new dependency). Shows the project's checks in plain language, the latest
  verdict, coverage bars, a pass/fail donut, a trend line across stored runs, per-check cards
  with raw output behind a disclosure, and an EMA duration estimate while a run is in flight.
- `labels.ts` gains `verification → "Final checks"`.

Test status at handoff: backend 532/532 (non-integration, `--filter "Category!=Integration&
FullyQualifiedName!~Opencode"`), GitCli 32/32, frontend 406 passed/1 skipped, `tsc` clean,
`next build` OK. Four `WorkflowResilienceTests` that had shipped red in `affe793` were fixed,
and pipeline-order tests updated for the 4th stage. Also fixed the pre-existing
`ReleaseWizard.tsx` unescaped-entity lint error that was breaking `next build`.

Not yet done: Phase 5 (versioned attestation archive, deep links from feature/release cards to
their checks, cross-release trends, real coverage thresholds in `devteam/readiness.yaml`), and
the release-level gate is enforced at the API layer (not inside `WorkflowEngine`), so an
in-process caller of `FinalizeReleaseAsync` would bypass it.

## Cancellation is now handled gracefully (UNCOMMITTED, 2026-09-21)
Cancelling a run used to surface as an unhandled `TaskCanceledException`: it was classified as a
`TimedOut` agent error (`RecordPromptFailureAsync`), escalated the stage to "needs retry", logged
at Error, emitted a bogus prompt-error event, and escaped the request as a raw 500.
- `BrokerCoordinator.PromptWithSessionRecoveryAsync` now catches `OperationCanceledException`
  first: logs Information, skips the prompt-error event, and rethrows (the existing
  `CancelCurrentTurn_...` test still passes).
- `WorkflowEngine`: the autonomous `RunStageAsync` catches the cancel inside `PromptRoleAsync`,
  sets a `cancelled` flag (`MarkPromptCancelled`: Active + no error + GuidedQA + a plain
  "Cancelled — run the stage again when you're ready." summary), unwinds the step loop, and
  **returns the release normally** — so re-running just works. `StartStageAsync`/`SendMessageAsync`
  mark the run cancelled (no escalation) and rethrow.
- `AgentErrorMiddleware` catches `OperationCanceledException` → 409 with "The run was cancelled."
  (no error log, no stack trace), so interactive paths never surface a raw 500.
- `BrokerApi.requestAsync` now uses the broker's plain-language error body as the thrown message
  (falling back to the technical form only for non-text bodies), so the UI shows
  "The run was cancelled." instead of "Broker POST /api/… failed with 409: …".
- NOTE: a debugger with "break when thrown" enabled will still stop at the throw point inside
  `JsonRpcConnection.SendAsync` — that's a debugger setting, not an unhandled exception.

## Run transparency: live activity, honest progress, remembered model (UNCOMMITTED, 2026-09-21)
Motivated by a real complaint: a long autonomous turn showed raw ids (`context_bundle`,
`verify_code`, `agent:qa`) and static lines ("Producing artifacts…", "waiting — the broker is busy
with another release right now") for 20+ minutes, which read as "hung".

**The bug that caused most of it.** `ReleaseWizard.tsx` compared `turn.acpSessionId` (the real ACP
id) against `stageRun.acpSessionId` (which actually stores the *DevTeamSession* id —
`WorkflowEngine` sets it from `session.SessionId`). They never matched, so `isRunningHere` was
always false and `isWaitingOnOtherTurn` always true: the screen claimed another release was being
worked on while *its own* run executed, and never showed elapsed time. `ActiveTurnInfo` now exposes
`StageRunSessionId` (named for what the UI correlates on) and `IsPriming`.

**What a running stage now shows**
- A real, in-memory **activity feed** (`/api/turns/current` → `ActiveTurnInfo.Activity`): tool calls
  (with status), coalesced "said this" snippets, and opt-in `Thinking` entries (checkbox, persisted
  in `localStorage` under `devteam.show-thinking`). Held and bounded in `ActiveTurnTracker`
  (~100 entries), populated from the events already flowing through `BrokerCoordinator.OnEventReceived`.
  Text/thought chunks are coalesced (≥2.5s apart) so a 30-minute turn can't flood the feed.
- A **heartbeat** (`LastEventAt`) — so "the agent has been quiet for 2 minutes…" replaces the old
  "no active turn detected" guess. `quietMessage`/`isQuiet` live in `Project/Release/activity.ts`.
- Honest waiting copy: names the blocking turn's elapsed, how many steps are **ahead of you**
  (`ActiveTurnInfo.QueuedTurns`), and that the run starts automatically.
- A **step progress bar** (`stepProgress`, aria-valuenow/max) and an **ETA** from this stage's own
  history (`estimateStageDurationMs`, EMA) plus an "taking longer than usual" note
  (`Project/Release/progress.ts`).
- Plain-language **step labels from the backend** (`StepFriendlyText`, exposed as
  `PipelineStageDto.StepLabels`); the raw id is now only the stable testid hook. `displayTitle` /
  `plainProblem` are used in the history and diagnostics panes too; the chat intro and push-back
  target use `stageLabel`.

**Also fixed along the way**
- `RunStageAsync` now sweeps its own exit-side gate rows before re-running (it lacked the sweep
  `RunGatesAsync` has), so a re-invoked run stops showing every step twice.
- `tool_call_update` is now **upserted** into the turn's tool calls instead of being broadcast only,
  so the persisted `Part` keeps the call's final output.
- **Remembered model (Phase E):** new `WorkspaceModelSettings` (`WorkspacePath` PK → `ModelId`,
  mirrors `WorkspaceProfileSettings`). `WorkflowEngine.ResolveModelIdAsync` resolves a new session's
  model as *workspace preference → `DefaultModelId`* (all 5 `NewSessionAsync` sites). Choosing a
  model anywhere — the stage-header `ModelPicker` or the provider-refused recovery pane, both of
  which land in `BrokerCoordinator.SetModelAsync` — also writes that preference via
  `RememberModelForWorkspaceAsync`, so you pick once and every later stage/release uses it.

Test status: backend 560/560 (non-integration, excluding real-opencode), frontend 433 passed /
1 skipped, `tsc` clean, `next build` OK. Six new frontend test files/additions cover the feed,
thoughts toggle, quiet copy, progress, ETA and the label path; backend adds `StepFriendlyTextTests`,
activity/queue/heartbeat tracker tests, and coordinator/engine model-preference tests.

Not done: **Phase D** — connecting the existing `/hub` SignalR so stream events *nudge* an immediate
refresh (polling stays the source of truth, so this is latency polish, not function), and a formal
"Now" panel restructure. Everything the panel was meant to answer is already on screen.

## A real hang, found and fixed (UNCOMMITTED, 2026-09-21)
Field report: a QA stage showed `running — 22m0s`, "the agent has been quiet for 22 minutes", and an
activity feed containing only "The agent started working". The broker log proves what happened:

    19:16:33 [INF] Prompting session "f6c9c02d-…" (isPriming=true, 7829 chars)
    19:38:38 [INF] Turn for session "f6c9c02d-…" was cancelled.
    19:38:38 POST /api/features/…/run-stage finished 200 elapsedMs=1324917   ← 22.1 min

Zero events for 22 minutes; the turn only ended because the user pressed Cancel. The broker stayed
alert and responsive throughout, so the agent (or its model stream) had gone silent and nothing
noticed. Root causes addressed:

1. **Silent event-loop death (hardening).** `JsonRpcConnection.ReadLoopAsync` only caught
   `JsonException` per frame — any other exception from a notification handler escaped, killed the
   ingest loop, and left every pending request waiting for its 30-minute RPC timeout with no events
   at all. It now catches per frame (and `FailPending` always runs via `finally`), so a broken
   connection surfaces immediately and one bad frame can't stop delivery.
2. **No stall detection (self-healing).** `BrokerCoordinator` now runs a per-turn watchdog: if the
   turn produces *nothing* for `StallAfter` (default 5 minutes, constructor-injectable), it cancels
   the turn and throws `AcpStalledException`. `WorkflowEngine.RecordPromptFailureAsync` maps it to
   the new `StageErrorKind.Stalled`, the stage goes `Escalated` with "The agent stopped responding
   — it produced nothing for minutes.", and `RunStageAsync` unwinds normally (200 + retry
   affordance) rather than surfacing a 500. A 22-minute hang is now a ~5-minute recovery.
3. **Heartbeat honesty.** `ActiveTurnTracker.Touch()` refreshes `LastEventAt` for *every* inbound
   frame (usage/config ticks included) while only some kinds add a feed entry — so a busy turn is
   never mistaken for a stalled one.
4. **Indicator + escape hatch.** `quietMessage` now distinguishes "quiet after real work" (probably
   thinking) from "hasn't produced anything at all" (looks stuck, cancel and retry). A
   **"Stop This Step"** button sits in the stage log while this stage's turn is running, so the
   escape hatch is where the problem is, not only in the global turn indicator.

Tests: backend 564/564 (non-integration, excluding real-opencode), frontend 435 passed / 1 skipped,
`tsc` + `eslint` clean. New coverage: read loop survives a throwing notification handler; the
watchdog stalls a silent prompt and leaves the slot free; an active turn is *not* stalled; a stall
escalates with `StageErrorKind.Stalled`; the UI shows the stuck wording and the Stop button.

Note: `next build` could not be re-run because the local `next dev` server holds `.next` (EPERM) —
jest/tsc/lint all pass, and the build succeeded earlier with everything except those last few UI
tweaks. Also: the running dev broker had to be stopped by exact PID to rebuild the exe (documented
safe), then restarted.

## Support diagnostics: verbose logging + one-file hand-off (UNCOMMITTED, 2026-09-21)
Goal: a non-technical user must be able to hand a real problem to a technical specialist without
either of them hunting through a machine.

**Turn on detail when something is wrong**
- `DiagnosticsSettings` (`Diagnostics/`) holds a persisted **verbose-logging** switch that drives a
  Serilog `LoggingLevelSwitch`: `Information` normally, `Verbose` when on (it includes EF's SQL and
  framework diagnostics — what actually reconstructs a failure). Persisted in the new generic
  `AppSetting` table, so it survives a restart mid-investigation, and restored at startup.
- The log file sink is now bounded (`fileSizeLimitBytes` 25MB, `rollOnFileSizeLimit`,
  14 retained files) so verbose mode can never fill the disk.
- Endpoints: `GET/POST /api/diagnostics/settings`, `POST /api/diagnostics/bundle`,
  `POST /api/diagnostics/reveal-logs` (opens the logs folder via `IFileSystemService`).

**High-resolution logging at the failure zones** (the broker previously had only ~16 Information
+ 5 Error calls in total, and the whole `Gates/` tree logged *nothing*):
- `GateRunner` — one line per check: name, feature, role, duration, and on failure the reason plus
  evidence (truncated). Every built-in check goes through here.
- `SystemProcessRunner` — every external command: file, args, cwd, timeout, exit code, duration,
  and on non-zero exit the stderr + stdout tail. Covers test runs, builds, linters, readiness.
- `ReadinessChecker` — per phase: command, duration, verdict, and the output tail on failure, plus
  a run summary.
- `WorkflowEngine` — one line per stage outcome (status/phase/attempt/allPassed).
- `BrokerCoordinator` — **time to first event**, per-turn event counts, queue wait for the single
  agent slot, and each stall-watchdog check. `timeToFirstEventMs = -1` is exactly what a stall
  looks like in the log (the 22-minute hang would have shown this).

**Correlation** — `RequestDiagnosticsMiddleware` now mints a short request id (honouring one
supplied by the client), echoes it as `X-Request-Id`, pushes it into the Serilog `LogContext`, and
appends it to failure bodies ("(reference: abc123)"). So a user can quote a reference and a
specialist can find that exact request.

**One-file hand-off** — `DiagnosticsBundleService` builds a zip: `app-info.json` (version, paths,
OS, framework), `settings.json`, `state.json` (recent releases/features/stage runs with their
check ledger, error kinds/messages, checkpoint, plus the live turn with its recent activity and
queue depth), `frontend-errors.json` (the UI's own recent failures, sent with the request), and
the last two log files (1MB tail each). Note `git-credentials.dat` is never included, and auth
tokens were already masked in the git logs.

**Frontend** — `app/UI/diagnostics.ts` keeps a bounded (50) localStorage ring of failures from
`window.onerror`, `unhandledrejection` and every failed API call (with status + request reference);
`BrokerApi.requestAsync` feeds it. New **Settings → Support** tab (`SupportView.tsx`): plain
instructions, the detailed-logging toggle, "Collect diagnostics file" (downloads the zip via
`saveBlob`), "Open the logs folder", the logs path, and a clearable count of recorded problems.

Tests: backend 576/576 (non-integration, excluding real-opencode), frontend 450 passed / 1
skipped, `tsc` + `eslint` clean. New: `DiagnosticsSettingsTests` (switch + persistence),
`FailureZoneLoggingTests` (gate pass/fail/throw, process success/failure), `ApiDiagnosticsTests`
(request reference echoed and honoured, verbose toggle round-trip, bundle contents),
`SupportView.test.tsx`, `diagnostics.test.ts`, and BrokerApi error-recording.

**Also fixed:** the "Stop This Step" button was `disabled={busy || cancelling}`, and `busy` is true
for the whole run — so the escape hatch was dead exactly when needed. It is now disabled only
while the cancel is in flight, with a regression test that starts a run (deferred promise ⇒ busy)
and asserts the button stays enabled.

Known issue: `ReleaseWizard.test.tsx › does not force-scroll to bottom once the reader has scrolled
up…` is intermittently flaky (fake timers + poller) — observed roughly 1 run in 3-8, pre-existing,
never reproduced under `--verbose`. Worth pinning down.

## The chosen model was never applied, and provider refusals were invisible (UNCOMMITTED, 2026-09-21)
Two defects behind "the UI says I picked model X but it clearly isn't using it":

**1. `NewSessionAsync` recorded the requested model and never told the agent.** `session/new`
carries only a cwd, so the model has to be applied with `session/set_model` — which never
happened. `opencode` therefore ran its own ambient model (`opencode/big-pickle`) while
`SessionDetail.ModelId` reported the request, and the picker displayed it. Evidence from
opencode's own log for the failing window: every app session streamed `opencode/big-pickle`
(the only `opencode-go/deepseek-v4.1-flash` streams were this assistant's own session).

Fixed:
- `NewSessionAsync` applies the model via `ApplyModelAsync`; if the agent refuses it, the
  ambient model is recorded instead and logged — the row now always matches reality.
- `DevTeamSession.RequestedModelId` added, surfaced on `SessionSummary`/`SessionDetail`, so a
  refusal shows as "asked for X, running Y" rather than silently succeeding.
- `RecreateAcpSessionAsync` re-applies the model **and** mode; without it a broker restart
  silently changed the model mid-release (and this path fires on every restart).
- `config_option_update` is now persisted — opencode tells us when its model changes, and we
  used to broadcast that to SignalR and throw it away.

**2. opencode never reports a provider failure over ACP.** It retries internally and the
`session/prompt` simply never returns, so turns hung until the 5-minute stall watchdog and the
user was told "the agent stopped responding". The cause was only in opencode's own log:
`message="stream error" ... error.error="AI_APICallError: Error from provider (Console): Rate
limit exceeded. Please try again later."` — 211 rate limits, 45 unavailable endpoints, etc.

Fixed (P2, partial — see below):
- `ProviderFailureClassifier` maps the agent's wording to rate-limited / unavailable /
  connect-failed / unknown-model, each with a plain sentence and a cooldown.
- `OpenCodeLogWatcher` tails `~/.local/share/opencode/log/opencode.log` for `stream error`
  against **this** turn's ACP session id (ignoring other sessions and older runs).
- `BrokerCoordinator` watches during a turn and, on detection, ends it in seconds with the
  provider's own reason instead of waiting out the stall budget.
- New `StageErrorKind.ProviderUnavailable`; `RunStageAsync` unwinds normally (200 + retry
  affordance) and the stage summary carries the reason. UI label added.

**P2 scaffolded but not yet wired:**
- `ModelCandidate` entity + `ModelCandidateService` (list/seed/add/remove/reorder/enable,
  `ResolveActiveAsync` skipping disabled and cooling-down entries, `RecordFailureAsync`).
- `ModelCatalog`: a curated seed of **10** models, `opencode/big-pickle` pinned first, then
  **strictly alternating providers** (**opencode** free / **opencode-go** paid / **google**) so a
  provider-level failure has a different provider to fall back to. Cost (1–5) and smartness (1–5)
  estimates per model, with `Score = smartness*3 − cost` documented; user-added models show
  "unknown" stats.
- Migrated a single-model `WorkspaceModelSettings` preference into the candidate list.

**Auto-switch (done):** on a detected refusal, `BrokerCoordinator.TrySwitchToNextModelAsync` cools
that model down, points the session at the next candidate (`session/set_model`), and **retries the
same prompt within the turn** — bounded by the list, so it escalates with the provider's reason
only when nothing is left. Each attempt gets its own linked token: cancelling the *turn* to abandon
a refused prompt would otherwise cancel the retry before it was sent (the test caught exactly that).

**Models screen (done):** Settings → **Models** (`ModelsView.tsx`) shows the ordered list with
`#1`/`#2`, per-row enable/disable, remove, ↑/↓ reorder, **visible cooldowns with the reason**
("Cooling down until 21:58 — The AI service is rate-limiting this model.") plus a notice explaining
that a run may pause before starting, and the read-only **Cost x/5 · Capability y/5** stats
("Stats: unknown" for a model with no estimate). Endpoints: `GET/POST
/api/models/candidates`, `DELETE /api/models/candidates/{id}`, `POST .../reorder`, `POST
.../{id}/enabled`, `GET /api/models/available`.

**Agent stderr (done):** `OpencodeAcpProcess` always drains stderr on a background task — a
redirected-but-unread pipe fills and blocks the child, which looks exactly like the agent going
silent. Lines are also logged, so the agent's own complaints reach our log.

**Desktop notifications (done, P3):** adapter pattern behind `IPlatformNotifier` —
`WindowsToastNotifier` raises a real Action-Center toast with sound by driving the WinRT API
through a PowerShell helper (no Windows-only TFM, no new package; swap the class for an in-process
adapter later), and `LoggingNotifier` is the **Linux/macOS stub**. `NotifierAdapterFactory` is a
runtime `OperatingSystem.IsWindows()` choice in DI. `UserNotifier` decides *what* is worth telling
someone — **stage complete**, **needs attention** (blocked/escalated), **approval needed** — with
an alarm sound for the attention cases, output "unknown"-style plain wording, and a 5-minute
repeat window so a polled stage can't spam. Preferences persist in `AppSettings` and are toggled
in Settings → Support, which also has **"Send a test notification"**
(`POST /api/notifications/test`) — the app is headless, so otherwise "did that work?" can't be
answered without waiting for a real event. Hooked from `WorkflowEngine` via an optional
`IUserNotifier` ctor param (so no existing test harness changed).

**Support bundle (done, P4):** now also carries `models.json` (the candidate list with cooldowns,
plus recent sessions' **requested vs applied** model — the "is my model actually being used?"
question), `opencodeLogPath` in `app-info.json`, and `logs/opencode.log` (the agent's own log,
512KB tail) — the only place a provider refusal is written.

**Still to do:** (a) measured cost per model — opencode reports a real `cost.amount` per turn
(`AgentTurnUsage`) that we still discard rather than persisting to the (unused) `ReleaseUsageLedger`,
so the cost stat is a curated estimate rather than observed data; (b) ACP wire trace at Verbose;
(c) real Linux/macOS notification adapters (`notify-send` / `osascript`).

Tests: backend 611/611 (non-integration, excluding real-opencode), GitCli 32/32, frontend 461
passed / 1 skipped, `tsc` + `eslint` clean. New this round: `ApiModelCandidateTests`,
`UserNotifierTests` (triggers, repeat window, prefs, sound downgrade, failing adapter),
`ModelsView.test.tsx`, the auto-switch coordinator tests, plus the P1/P2 units listed above.

Known issue: `ReleaseWizard.test.tsx › does not force-scroll to bottom once the reader has scrolled
up…` is intermittently flaky (fake timers + poller) — roughly 1 run in 3–8, pre-existing.

## Todo backlog
- [done] SelectWrapper + mode picker (full stack) - committed, smoke-tested.
- [pending] Todo 9: Avalonia shell (ShipRight pattern: folder picker -> spawn
  broker -> health -> WebView2). Template: D:\work\nyingi\code\systems\ShipRight
  (Avalonia 12.0.4, WebView2, tray, InnoSetup).
- [pending] Todo 10: E2E + InnoSetup packaging.

## Conventions
- TDD (xUnit backend in DevTeam.Tests; Jest+RTL frontend). Never report done
  without a green suite. Composition over modification (new methods/classes over
  editing working methods).
- Broker REST JSON is camelCase (web defaults). StreamEvent type strings:
  textDelta, thoughtDelta, toolCall, toolCallUpdated, usageUpdated,
  configOptionsUpdated, error, turnEnd.
- ComplianceKit: ~<=40-line funcs, DRY; `naming_mixed` flags on wire JSON keys
  are false positives; JSON-RPC error codes extracted as constants.
- Notify sender: "big-pickle". Broker listens loopback 5202 by default
  (RuntimeIdentity.DefaultPort). Keep `Program` `public partial` for
  `WebApplicationFactory<Program>`.