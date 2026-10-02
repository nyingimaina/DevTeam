# slow-trudge.md — the full diagnosis and repair record of the DevTeam broker stability war

Written: 2026-10-01, ~22:00 local (+03:00), on `feature/better-ui`, by the operator's build session.
Scope: every failure mode observed in the running system over the last ~3 weeks (2026-09-13 → 2026-10-01),
with evidence, counts of recurrence, the history of every fix campaign attempted, and the repair plan now
in flight. This file exists so that nobody — human or agent — ever has to re-derive this story from
5.9 MB of interleaved Serilog files again.

---

## 0. How to read this file

- **Evidence tags** look like `[log devteam-20261001_034.log @21:43:34.585]`, `[git 410404c]`,
  `[api GET /api/releases @21:48]`, `[proc PID 26612]`. Every claim below traces to one.
- **"The storm"** = any event where two or more broker processes were alive at the same time fighting
  over one SQLite database and one set of Serilog log files.
- **"The invisible turn"** = an agent (`opencode.exe acp`) doing real work with no tracked turn, i.e.
  invisible to the UI, the Stop button, and the stall watchdog.
- **"The stage loop"** = the pipeline re-running stages (or the same class of stage) repeatedly without
  converging: attempt counters climbing, "stage complete" commits landing, work not advancing.

---

## 1. Executive summary

DevTeam's broker is a long-lived local process that owns a SQLite database (`~/.devteam/devteam.db`),
a Serilog file set (`~/.devteam/logs/devteam-YYYYMMDD_NNN.log`), a loopback HTTP port (5202), and a
single `opencode.exe acp` child that it drives over JSON-RPC. Almost every stability failure we have
observed in three weeks is one of five broken **conservation invariants** — things that must be
exactly-one but were allowed to become many-or-none:

| # | Invariant (what must be true) | Observed violation (what actually happened) |
|---|---|---|
| 1 | Exactly **one** live broker per (port, data-dir) | **Nine** broker lifetimes in 16 seconds on 2026-10-01 21:43:18–34; ≥5 more at 10:19:44–51 the same morning; two bursts on 2026-09-28 (19:15:30, 19:18:02) |
| 2 | Every **write** to the DB comes from the current owner | A `POST /api/releases` that answered **201** and whose insert then **vanished** (transaction lost when its broker died mid-flight) `[log _034 @21:43:34.571–.585]` |
| 3 | Every busy agent child maps to a **tracked turn** | `opencode.exe acp` PID 14448 burning ~2 CPU cores for 15+ minutes while `GET /api/turns/current` answered **204** (nothing running) `[proc @21:42–21:57]` |
| 4 | Every **retry** is charged to a bounded budget | Subtraction feature's QA stage ran **12 attempts** (9 stalled, 1 timed out, 1 lost to a broker restart, 1 gate-blocked) before a human intervened; `ConsecutiveFailures` read 0 on every row in the DB — the budget resets in practice |
| 5 | **Requests** are served only by the license holder | `advance` → 404, `start-stage` → 503 flapping as the UI's requests bounced between dying brokers during storms |

Everything in the repair plan (section 7) enforces these five invariants mechanically — no judgment
calls, no heuristics — plus an LLM "coroner" layer for the one failure class that invariants provably
cannot catch (semantic loops: stages that pass gates while converging on nothing).

---

## 2. The incident of 2026-10-01, evening — minute by minute

The operator restarted the desktop app at ~21:41 and within three minutes had: two brokers fighting,
a lost database write, an invisible agent turn, and a UI that kept scrolling away from them. This is
the incident that triggered the current repair campaign. Timeline, all times local (+03:00):

- **21:41:53** — `DevTeam.Desktop.exe` (PID 8632, launched from Explorer) spawns a new broker:
  `DevTeam.Broker.exe --port=5202 --data-dir=C:\Users\nying\.devteam` (PID 26612). `[proc]`
- **21:41:54–58** — that broker resolves the opencode CLI, runs its **crash-recovery pass**
  ("GatesRunning runs healed to Active, lost-turn Active runs escalated to Disconnected", 1278 ms),
  prints "Application started". `[log _025 @21:41:54–21:41:59]`
- **21:42:01** — the broker spawns its `opencode.exe acp` child (PID 14448). **No HTTP request has
  arrived yet.** Something at boot — session recovery or an auto-retry path — started agent work
  before any user action. This spawn is the subject of repair item D6. `[proc]`
- **21:42:18** — a git-status probe (`isRepo: false` on a non-repo path) — the desktop UI is open and
  polling. `[log _025 @21:42:18.776]`
- **21:42:44** — `POST /api/features/aecd77c2…/run-stage` arrives (the better-ui feature's
  **verification** stage, attempt 2 — see §4.6). The broker primes the session
  ("Prompting session … isPriming=true, 7423 chars" at 21:42:47). `[log _025]`
- **21:43:12** — a model switch is queued mid-turn ("switch model to opencode/big-pickle (queued)") —
  the session had drifted off the pinned model. `[log _025]`
- **21:43:18–21:43:31** — **the storm.** Nine fresh broker lifetimes start within 13 seconds, each
  leaving its own Serilog file segment (`devteam-20261001_026.log` … `_034.log`, first lines
  21:43:18.658, 21:43:19.714, 21:43:25.521, 21:43:25.680, 21:43:27.622, 21:43:28.427, 21:43:28.828,
  21:43:31.092, 21:43:31.707). All nine are dead now; their parent PIDs died before inspection, so
  **which actor spawned them is not conclusively identified** (candidates: the desktop shell's
  retry-on-unhealthy path, a second shell instance, or an operator rapidly retrying). Repair item D3
  makes the question moot by construction: any spawn attempt against a healthy broker becomes a no-op
  (adopt-don't-spawn). `[logs _026–_034 first lines]`
- **21:43:30–34** — the day-long log file `_025` (the surviving broker's) and the nine storm files are
  all being written **simultaneously**. One second of `_034` contains requests being served
  (`run-gates` 200 at .471), another broker starting up ("Application started" at .583), and one
  shutting down ("Application is shutting down…" at .585) — interleaved lifetimes sharing one file.
  Log-to-instance attribution is genuinely tangled; that is itself part of the damage. `[log _034]`
- **21:43:32** — someone toggles **verbose logging** on (`POST /api/diagnostics/settings`), which is
  why later tails are full of `[DBG]` EF SQL lines. `[log _030 @21:43:32.794]`
- **21:43:34.471–.519** — while one broker executes the better-ui verification stage, another
  (storm-born, briefly serving) answers: `run-gates` for feature `fa2bb0d6…` → **200**, then `advance`
  for feature `f7111cac…` → **404 (feature not found)**. Mixed feature IDs and mixed responders —
  invariant 5 violated in one second. `[log _034]`
- **21:43:34.548–.585** — `GET /api/turns/current` → 204 (nothing tracked), then `POST /api/releases`
  → **201** with `INSERT INTO "Releases"` + `ReleaseFeatures` + `ReleaseSignoffs` in flight, and
  **14 ms later the serving broker dies mid-transaction**. The database today shows **no release
  created after 2026-09-28** — the 201'd insert was rolled back and lost. Invariant 2 violated:
  the UI almost certainly displayed success for a release that does not exist. `[log _034; api @21:48
  — newest release is "better-ui", created 2026-09-28 17:31]`
- **21:44:08–09** — the verification-stage turn is cancelled ("Turn for session … was cancelled";
  `turns/current` 204 again). The 105.8-second `run-stage` request then returns **200** — a cancelled
  turn surfaced as a normal completion with a retry affordance, per the earlier cancellation work. The
  stage lands `BlockedGate`, "Stage verification failed gates or challenge", attempt 2. `[log _025
  @21:44:30.188; api — verification attempts 1 & 2 both BlockedGate]`
- **21:44:50–21:46:13** — the OpenCode *Desktop* app (Electron, PID 30900, launched from Explorer —
  unrelated to the broker) starts, spawning six `OpenCode.exe` children (gpu/renderer/network/…).
  Initially mistaken for orphaned broker agents during triage; process parentage proved otherwise.
  Recorded here so the next person doesn't repeat the misread. `[proc Win32_Process parentage]`
- **21:42 → 21:57+** — **the invisible turn.** The acp child PID 14448 (spawned at 21:42:01, before
  any request) burns CPU continuously — 15 CPU-minutes by 21:49, 25.2 by 21:53, 27.6 by 21:54:52,
  30.6 by 21:57 — a steady ~2 cores — while `/api/turns/current` answers 204 the whole time. No
  tracked turn, no activity feed, no heartbeat, no Stop button, no stall watchdog (all of those hang
  off the ActiveTurnTracker). Root cause not yet fully pinned; candidate mechanisms: session recovery
  re-prompting without registering a tracked turn, or opencode-internal work started by the recovery
  session. This is repair items D4 (child registry + orphan sweeper) and D6 (recovery consent). `[proc
  CPU samples; api turns/current 204 @21:44:09, 21:49:27, 21:53, 21:54:29]`
- **21:49 → 21:57** — the surviving broker is healthy and serves every request (healthz, releases,
  metrics all 200) but writes almost nothing to any log file between request bursts — the log silence
  that made the evening look like a total hang from outside.

**What the incident was NOT:** the broker did not crash-loop on its own after 21:43:34. One broker
(PID 26612) survived and serves to this moment. The damage was done in the 16-second window when
nine others were alive.

---

## 3. The morning of 2026-10-01 — the same storm, different clothes

- **10:19:44–10:19:51** — a ≥5-lifetime broker burst: "opencode CLI resolved" lines at 10:19:44.760,
  45.802, 46.372, 49.717, 50.632; three crash-recovery passes ("finished in 14 ms / 347 ms / 1 ms");
  `start-stage` 503s at 10:19:45.854, 45.937, 49.731 and a `run-stage` 503 at 10:19:49.758 (requests
  arriving while brokers were still fighting to boot); and the same interleaved tell — "Application
  started" at 10:19:51.073 with "Application is shutting down…" 2 ms later. `[log _025 morning block]`
- **09:31–10:18** — the better-ui stage loop's second round ran to completion *while the storm pattern
  was already established*: test-runner attempts 2→5 (06:31–06:55 UTC), developer attempt 4
  (06:44), qa attempt 1 (06:57), gate-prompt turns (06:50, 07:06), finishing with the qa "stage
  complete" commit at 10:18 local. `[api /api/metrics/turns?days=1; git 0346fc7, 410404c]`

---

## 4. The recurring failure classes — and how many times each has hit

Counted from the DB (via the live API), git history, and log archives. These are the numbers that
justify calling the fixes a *campaign* rather than patches.

### 4.1 Broker restart storms (invariant 1)
- **2026-09-28, 08:54:20** — an unclean broker restart swept the DB and marked every then-Active run
  across **three stale releases** (todo 1.0.0, todo 1.0.1, todo 1.0.2) as `Disconnected` "The broker
  restarted mid-prompt and the agent session was lost." `[api — rows with lastErrorAt
  2026-09-28T08:54:20.975…976]`
- **2026-09-28, 19:15:30 and 19:18:02** — two storm bursts on record in the comment of fix commit
  `07cfac9` ("a test host builds Program with the DEFAULT connection string — the production
  devteam.db — and its boot-time recovery pass escalates the live app's mid-prompt run"). This one
  was **test hosts fighting the live broker through the shared DB**, fixed by DB-ownership heartbeats.
- **2026-10-01, 10:19:44–51** — ≥5 lifetimes (§3).
- **2026-10-01, 21:43:18–34** — 9 lifetimes in 16 seconds (§2).
- **Today's log segments alone**: `devteam-20261001_001.log` through `_034.log` — 34 file segments in
  one day, of which 11 (`_024`–`_034`) were written in a 5-minute evening window.
- **Total: ≥16 documented extra broker lifetimes across ≥4 storm events in 5 days.**

### 4.2 Lost/misattributed writes (invariant 2)
- The 201'd release insert that vanished (2026-10-01 21:43:34, §2) — the only *confirmed* lost write,
  but it required nothing but bad luck: a request served by a broker that died 14 ms later.
- Nine storm brokers each ran a crash-recovery pass against the shared DB before dying — the
  classification writes themselves were duplicated across lifetimes.

### 4.3 Invisible/untracked agent work (invariant 3)
- 2026-10-01 evening: PID 14448, ~2 cores for 15+ minutes, zero tracked turns (§2).
- The historical version of the same class: the "22-minute hang" (2026-09-21, QA session `f6c9c02d…`)
  where a turn produced nothing for 22 minutes, only ended because the operator pressed Cancel —
  that one was *tracked* but unwatched; tonight's was *untracked entirely*, which is strictly worse:
  no watchdog could even see it.

### 4.4 Unbounded retry loops (invariant 4)
- **Subtraction feature (calculator workspace), 2026-09-21** — the worst recorded loop: QA stage
  attempts **1 through 12** (nine `Stalled` — "The agent produced no output for 5 minutes" — one
  `TimedOut` at attempt 1 after a 5-hour window, one `BlockedGate` at attempt 2, and the terminal
  attempt 12 killed by a broker restart `Disconnected`). A human finally cancelled the run at 04:17
  the next morning. `[api — 12 stage-run rows for feature 26d4400a…]`
- **Rate-limit storm (2026-09-21 era)** — opencode's own log showed **211 provider rate-limit errors
  and 45 unavailable-endpoint errors** while turns hung until the stall watchdog; the broker never
  saw a word of it over ACP (fixed by the provider-failure classifier + log watcher, but the class
  recurs whenever a new refusal wording appears).
- **better-ui test-runner, 2026-10-01 morning** — attempts 2, 3, 4 to pass (each ~1–5 min of tokens)
  before attempt 5 went green. `[api metrics]`
- **better-ui verification, 2026-10-01 evening** — attempts 1 and 2 both `BlockedGate` — this is the
  loop **currently live** when this file was written.
- **`ConsecutiveFailures = 0` on every row in the DB** despite all of the above — the budget that was
  built (GateFailureLoopGuard, Max 3) resets across attempt rows and restarts in practice.

### 4.5 Request flapping (invariant 5)
- 404 `advance` + 503 `start-stage`/`run-stage` bursts during both 2026-10-01 storms (§2, §3).

### 4.6 The semantic stage loop (the class invariants can't see)
- **better-ui, two full pipeline rounds** in three days: business-analyst complete 2026-09-28→29
  `[git 400554a]`, developer complete 09-29 `[git 9172fdf]`, then after the 0.1.14 merge a **second
  developer round** 10-01 09:54 `[git b26a2f4]`, test-runner 09:56 `[git 0346fc7]`, qa 10:18
  `[git 410404c]` — each landing a "stage complete" commit, then advancing to `verification`, which
  failed its gates twice tonight. Work converges slower than attempts accumulate. This class is why
  the plan adds an LLM "coroner" (E) rather than more counters.
- During the same window, unrelated work kept landing on this workspace and had to be parked manually
  (`git stash@{0}`: "park unrelated gate-reuse/re-run-step workstream off this workspace"), which is
  the workspace-hygiene version of the same disease: too many actors, one working tree.

### 4.7 Frontend: illegibility and scroll-yank (the operator-facing half)
- **Contrast**: `ReleaseWizard.module.css` was written dark-first and half-migrated. It uses
  `var(--text-color, #eee)` in 10+ places — **`--text-color` is not defined anywhere** (globals.css
  defines `--text-body`/`--text-heading`), so the fallback `#eee` always wins: near-white text on
  white surfaces in the default light theme, i.e. invisible. Same undefined-var class:
  `var(--border-color, #333)`, `var(--hover-bg, #1e1e1e)`. Status text is hardcoded Tailwind-400
  colors (`#60a5fa`/`#4ade80`/`#fbbf24`/`#f87171` — `.logPhase`, `.logLineOk`, `.logLineWarn`,
  `.logLineBad`, plus the ✓/✗ gate lines and status rows) — those are dark-theme colors sitting on
  light surfaces at **1.6:1–3.3:1** contrast (WCAG AA requires 4.5:1). The stage log's body text is
  `--text-muted` at 12 px monospace. `[ReleaseWizard.module.css lines ~101–116, 261–265, 338–346,
  769–823; globals.css — no --text-color defined]`
- **Scroll yank**: while a run is live, three pollers drive re-renders (release 3 s, transcript 2.5 s,
  current-turn 2 s). On every tick, effects call `messagesEndRef`/`logEndRef`.
  **`scrollIntoView({block:"nearest"})`** — which scrolls *every scrollable ancestor* as needed. The
  stickiness guard (`stickToBottomRef`) only updates from the **inner** container's `onScroll`, so
  scrolling the **page** never disarms it (default: armed). Operator scrolls down to lower sections →
  the end-marker is above the viewport → every 2–3 s the page is yanked back up to reveal it. A
  secondary yank: `chatInputRef.current?.focus()` re-fires on every busy/sending/gates transition and
  browsers scroll focused elements into view. The existing regression test only covers the
  inner-container case and is **flaky 1-in-3–8 runs** (fake timers + poller race), which is why the
  bug shipped. `[ReleaseWizard.tsx ~1547–1660, ~1899–2028; ReleaseWizard.test.tsx ~398–426;
  HANDOFF "known issue"]`
- **Operator judgment invisible**: every human-decision surface is inline and mid-page — signoff
  approve (`showReview`/`showApproveNow`/StageCompleteCard), NegotiationPanel (push-back points),
  Escalated-awaiting-retry, BlockedSignoff ("waiting for your approval above"), foreign-process
  approvals. Nothing blocks, nothing promotes, nothing makes a sound when a decision appears — the
  operator discovers them by scrolling around, which §scroll-yank actively prevents.

---

## 5. Root-cause statement (one paragraph)

The broker's ownership of its resources (port, DB, log files, agent children, retry budgets) is
**conventional, not enforced**: any process that starts may open all of them; any component that
prompts an agent may do so without registering a tracked turn; any restart may re-run recovery and
retries without paying into a persistent budget; and the UI trusts whichever broker answers a request.
Under single-instance, everything-works conditions this never shows. Under *any* anomaly (a health
probe timeout, a port race, a fast-fingered retry, a crashed predecessor), the system enters a mode
where multiple actors mutate one state and nobody can attribute anything — the operator experiences
this as "stuck in another loop," "the app ate my release," and "text I can't read while the page
fights me." The fix is not more cleverness in the loop bodies; it is **enforcing the five invariants
mechanically at the boundaries** (mutex before side effects; adopt-don't-spawn at the spawner; a
registry reconciling children against tracked turns; budgets that survive restarts; identity-rich
healthz) — and an evidence-grounded LLM advisor for the semantic-loop class that no invariant can
express.

---

## 6. History of the fix campaigns (2026-09-13 → 2026-10-01)

Each entry: what broke, what we shipped, whether it held. "Held" means the specific failure mode never
recurred — several fixes held perfectly while *adjacent* classes produced tonight's incident.

1. **Week of 2026-09-08/13 — the first "hang."** Broker never sent ACP `initialize`, so
   `session/new`/`session/prompt` waited forever on a handshake opencode would not give.
   Fix: lazy `initialize` in `BrokerCoordinator.EnsureInitializedAsync` (plus the `IAcpProcess →
   OpencodeAcpProcess` DI fix and `RuntimeIdentity.OpenCodePath` from `19f476f`). **Held** — no
   handshake hang since.
2. **2026-09-13 — wire/format bug batch.** Streaming showed "undefined…" (payloads serialized
   PascalCase while the UI reads camelCase); side-pane list button was dead
   (`ZestResponsiveLayout` never rendered); plus app-wide theme, semantic buttons, progress bars,
   reduced-motion polish. Fix: `PayloadJsonOptions` web-defaults, layout re-wiring. **Held.**
3. **2026-09-21 — ship-readiness gate + Checks library.** Two enforced gates: per-feature
   deterministic `verification` stage; per-release sha-pinned `ReadinessAttestation` before protected
   merges (main/master refused without a passing attestation at the matching commit). Frontend Checks
   tab (recharts). **Held for its scope** — but note the *verification* stage itself is what is
   looping tonight (§4.6); a gate can be honest and still be the thing that trips repeatedly.
4. **2026-09-21 — cancellation grace.** Cancels surfaced as raw 500s / "TimedOut" escalations.
   Fix: `OperationCanceledException` caught first (coordinator → engine → middleware → UI message).
   **Held** — tonight's 21:44 cancel returned a clean 200 with a retry affordance exactly as designed.
5. **2026-09-21 — run transparency.** The "is it working?" problem: raw ids, static lines, 20-minute
   silent turns misread as hung. Fix: live activity feed, heartbeat (`LastEventAt`), queued-turns
   copy, step progress + EMA ETA, `StepFriendlyText`, remembered workspace model. **Held — for
   tracked turns.** Tonight's invisible turn (§2) is this fix's blind spot: the feed only knows what
   the ActiveTurnTracker knows.
6. **2026-09-21 — the 22-minute hang.** A notification handler exception killed the JSON-RPC read
   loop silently; every pending request then waited on the 30-minute RPC timeout; a per-turn watchdog
   didn't exist. Fix: per-frame catch + `finally FailPending`; **5-minute stall watchdog** (cancel +
   `AcpStalledException` → `Escalated` with a plain reason); heartbeat honesty; "Stop This Step"
   button. **Held for tracked turns** — 9 of the subtraction stalls (§4.4) were this watchdog doing
   its job correctly; what it cannot do is see an *untracked* child.
7. **2026-09-21 — diagnostics.** Nothing could be handed to a specialist. Fix: persisted
   verbose-logging switch driving a `LoggingLevelSwitch`, bounded file sink (25 MB × 14), failure-zone
   logging (GateRunner / SystemProcessRunner / ReadinessChecker / WorkflowEngine / BrokerCoordinator
   one-liners incl. time-to-first-event), request ids, one-file support bundle. **Held** — this
   evening's diagnosis was only possible because of it (the `[DBG]` tails, the request-id-less but
   timestamped storm forensics).
8. **2026-09-21 — the model that was never applied + invisible provider refusals.** `session/new`
   carried only cwd; the chosen model was recorded but never sent (`session/set_model` never called),
   so opencode ran its ambient model while the UI claimed otherwise; provider errors (rate limits)
   never crossed ACP, so turns hung until the stall watchdog. Fix: `ApplyModelAsync` at session
   create + `RequestedModelId` surfaced ("asked for X, running Y"); `ProviderFailureClassifier`;
   `OpenCodeLogWatcher` tailing opencode's log per ACP session; `ModelCandidate` list (10 curated,
   alternating providers, cooldowns); **auto-switch + in-turn retry**; Models screen; stderr drain;
   desktop toasts (`UserNotifier` — stage complete / needs attention / approval needed, 5-min repeat
   window); support bundle carries `models.json` + opencode log. **Mostly held** — provider refusals
   are detected and turned into seconds-not-minutes; the recurring residue is the semantic-loop class
   (§4.6), which no amount of classifier plumbing addresses.
9. **2026-09-26/28 — ownership & resilience line (releases 0.1.9–0.1.11).**
   `3a7d08c` "kill only what we own, ask for the rest" (process sweep asks before killing foreign
   processes — the ProcessApprovalNotice flow); `ff52c13` "never start opencode eagerly, never fail an
   unrelated request on one launch" (DeferredAcpProcess — read-only endpoints no longer spawn the
   agent); `210afe0` "context tracking, sign-off auto-approval, and startup crash recovery"
   (WorkflowCrashRecoverer + BrokerHeartbeatService); `07cfac9` "heal only when no live broker owns
   the database" (DB-ownership heartbeat — the 2026-09-28 19:15/19:18 test-host-vs-live-broker storms);
   `d6e455b` consolidation onto master. **Each held for its exact trigger** — but all of them police
   *behavior after* multiple brokers exist. None *prevents* the second broker. Tonight proved the gap:
   the heartbeat protects the DB's *recovery* path, while requests, logs, and one insert still went
   through doomed transients.
10. **2026-09-28 → 10-01 — the better-ui feature line** (BA `400554a`, dev `9172fdf`, merge to pick up
    0.1.14 `f9e39b4`, test-runner judgment `11741ed`/`8fbcd5b`/`433461b`/`446c3cc`/`0683783`, gates
    perf `cb3df10`, second dev round `b26a2f4`, test-runner `0346fc7`, qa `410404c`). These are the
    pipeline working on itself — and demonstrating §4.6 while doing so.

**The pattern in the history:** every campaign fixed the *observed* symptom class and held; the
incidents kept coming because each new incident was a different **invariant violation** that no prior
fix covered. We have now enumerated the invariant space (section 1) and are closing all of it at once.

---

## 7. The repair plan now in flight (status as of writing)

Sequencing agreed with the operator: **D → B → A → C → E**, then version bump + full build. TDD
throughout (red test first; suites green before "done").

- **D — broker integrity (enforces invariants 1, 2, 3, 4, 5)**
  - **D1 Single-instance license — DONE this evening.** Machine-global named mutex
    `Global\DevTeam.Broker.<sha256(dataDir)[..16]>` acquired in `Main` *before any side effect*
    (`BrokerLicenseGate.TryEnter` before `Directory.CreateDirectory`/`WebApplication.CreateBuilder` —
    pinned by a source-order test). Refused → one stderr line (never the shared Serilog file) + exit
    code **73** (`BrokerExitCodes.AlreadyRunning`) so the desktop can tell "refused on purpose" from
    "crashed." Handles abandoned-mutex takeover from a crashed predecessor. Test-host opt-out via
    `DEVTEAM_LICENSE_DISABLED=1` set by a module initializer in the test assembly (the first attempt
    used `ASPNETCORE_TEST_CONTENT_ROOT`, which empirically is *not* set when factory-hosted `Main`
    runs — 89 suite failures taught us; the module initializer is explicit and race-free). 12/12
    licensing tests green; full non-integration suite **1068/1069** (the 1 failure is pre-existing —
    see §8). Files: `Shared/BrokerIdentity.cs`, `Shared/BrokerExitCodes.cs`,
    `Broker/Licensing/{NamedMutexBrokerLicense,BrokerLicenseGate}.cs`, `Program.cs` wiring,
    `Tests/Licensing/BrokerLicenseTests.cs`, `Tests/TestHostLicenseOptOut.cs`.
  - **D2 identity-rich /healthz — DONE.** `pid` + `bootId` (fresh Guid per broker lifetime) +
    `dataDirHash` (same fingerprint as the mutex), via `BrokerBootInfo` singleton; verified by
    `HealthzBootInfoTests` (bootId stable within one host, pid exact in-process, fingerprint equals
    the caller-computable value).
  - **D3 adopt-don't-spawn — NEXT.** `BrokerProcessManager.StartAsync` probes `/healthz` first:
    healthy + matching `dataDirHash` → `Ready()` with zero spawning; port held by a foreign/unhealthy
    process → plain-language failure; a spawned child exiting 73 → adopt the live broker. Restart
    backoff+jitter to make storms expensive. Kills the spawning loop class regardless of which actor
    causes it.
  - **D4 child registry + orphan sweeper.** Every spawned `opencode.exe acp` registered (pid,
    sessionId/stageRunId, purpose); sweeper reconciles live children against the ActiveTurnTracker
    (piggybacking the heartbeat service): orphan → **contain** (stop routing work), notify via the
    existing `UserNotifier` (toast + alarm), offer an **exact-PID stop** — never kill by name, never
    auto-kill.
  - **D5 budgets that survive.** `ConsecutiveFailures`/`LastFailureSignature` inherited across attempt
    rows and restarts (today they read 0 everywhere — the guard resets in practice); only a *different*
    failure signature or an explicit operator override resets the budget.
  - **D6 recovery consent + boot-spawn audit.** Crash-recovery keeps auto-*classifying* (bookkeeping),
    but *resuming* interrupted runs becomes an explicit operator decision surfaced with notification
    + audio; and the 21:42:01 boot-spawn path gets audited so no ACP child exists without a live
    tracked turn or a user action.
- **B — contrast (AA 4.5:1, keep the green accent) — PENDING.** Define the missing tokens
  (`--text-color`, `--border-color`, `--hover-bg`) in both themes; add `--status-ok/warn/err/info`
  (light: 700/800-series; dark: current 400s); sweep every hardcoded status hex; `.logLine` body at
  readable contrast; lock it with a Jest contrast guard (parses CSS modules, fails below 4.5:1, fails
  on undefined `var(--x, fb)`).
- **A — scroll-yank — PENDING.** Auto-follow scrolls the inner container only (`el.scrollTop =
  el.scrollHeight`, never `scrollIntoView`); stickiness disarms when the pane leaves the viewport;
  `focus({preventScroll:true})`; the flaky test rewritten deterministically + a new regression test
  for outer-pane scroll during live polling.
- **C — AttentionBar — PENDING.** Sticky global bar beside `ActiveTurnIndicator` aggregating every
  pending human decision (signoffs, open negotiation points, escalated-awaiting-retry,
  resumption-required from D6, foreign-process approvals); plain-language sentence + inline primary
  action + click-through; `role="alert"`; new items raise `UserNotifier` **notification + alarm
  audio** (5-min repeat window, audio toggle already persisted in Settings → Support); desktop toast
  deep-links into the wizard via the existing SPA-fallback routes.
- **E — the coroner — LAST.** Evidence-pack builder (reusing the diagnostics-bundle shapes) → typed
  verdict (enum + confidence + cited evidence + recommended action) → findings land in the AttentionBar
  with toast + audio; the operator remains the only approver; the coroner itself is budgeted by the
  model-candidate cooldown machinery. Covers the semantic-loop class (§4.6) that invariants cannot.

Then: **version bump (0.1.15 → 0.1.16) and full build** (`build.ps1` publish + smoke checklist from
HANDOFF: healthz, session create, mode switch, prompt end_turn, persistence, kill by PID only).

---

## 8. Known pre-existing issues (not caused by the current campaign)

- **`BrokerHubTests.Hub_StreamsPromptEventsToJoinedClient` — failing on the clean tree.** 404 on
  `POST /api/features/{id}/start-stage`. Verified pre-existing by stashing all current changes and
  re-running (still fails). It was masked in earlier "green suite" claims by runs that didn't include
  it or by category filtering; it needs its own fix (the test drives a release through the real
  pipeline and the feature/start-stage path 404s — likely a route/payload drift after the session
  routes were removed from the Chat tab era). Tracked separately; do not let it be mistaken for a
  regression from D1–D2.
- **`ReleaseWizard.test.tsx › does not force-scroll to bottom once the reader has scrolled up…`** —
  flaky 1-in-3–8 (fake timers + poller race), pre-existing, and the live symptom is §4.7's scroll
  yank. Fixed as part of workstream A rather than papered over.
- **`--text-color` & friends undefined** — the illegibility root (§4.7); until B lands, the light
  theme genuinely hides text; dark mode hides less but still fails AA on status colors.

---

## 9. Evidence index (where each fact lives)

| Evidence | Where |
|---|---|
| Storm log segments | `~/.devteam/logs/devteam-20261001_0{21..34}.log` (first lines = broker start times) |
| Interleaved lifetimes / lost 201 insert | `devteam-20261001_034.log` @21:43:34.471–.585 |
| Survivor's evening (crash-recovery, run-stage, cancel) | `devteam-20261001_025.log` @21:41:54–21:44:30 |
| Morning burst + 503 flapping | `devteam-20261001_025.log` @10:19:44–10:19:51 |
| Process genealogy (who spawned whom) | `Get-CimInstance Win32_Process` snapshot taken 21:53: desktop shell PID 8632 → broker 26612 → acp 14448; Electron parent 30900 from Explorer |
| Port ownership | `Get-NetTCPConnection -LocalPort 5202` → both loopback endpoints owned by 26612 |
| DB state (releases, stage runs, attempt counters, error kinds) | `GET /api/releases`, `GET /api/releases/{f889efb9…}` (better-ui), `GET /api/metrics/turns?days=1` on the live broker |
| 12-attempt subtraction loop | stage-run rows for feature `26d4400a…` (calculator workspace), 2026-09-21 |
| 2026-09-28 restart sweep | rows with `lastErrorAt 2026-09-28T08:54:20.97…` across three stale releases |
| better-ui two-round loop | `git log feature/better-ui` — 400554a, 9172fdf, b26a2f4, 0346fc7, 410404c |
| Parked workstream stash | `git stash@{0}` ("park unrelated gate-reuse/re-run-step workstream…") |
| Contrast/scroll/approval code sites | `front-end/app/globals.css`; `front-end/app/Project/Styles/ReleaseWizard.module.css` (~27–116, 261–346, 769–823); `front-end/app/Project/Release/ReleaseWizard.tsx` (~1547–1660 chat, ~1899–2028 log, pollers ~381–392/1612–1624/1917–1924, signoff ~394–512, `NegotiationPanel` ~1046); `App.tsx` ~80–83 (notices); `DevTeam.Desktop/Services/BrokerProcessManager.cs` |
| Fix history & conventions | `HANDOFF.md`; `git log` 08e1d95…410404c |

*End of record. The trudge continues: next action is D3 (adopt-don't-spawn + restart backoff in the
desktop shell), per §7.*
