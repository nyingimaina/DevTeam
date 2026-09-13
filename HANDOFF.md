# DevTeam — Handoff / Status

Last updated: 2026-09-13 (mode-picker feature COMMITTED; repo clean).

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
- Test counts: backend 53/53 green (non-opencode-integration; full suite runs 2
  real-opencode tests, >10 min); frontend 21/21 green; `next build` + `next
  lint` clean; full `build.ps1` publish OK.

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
  13 frontend tests green.
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