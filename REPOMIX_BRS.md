# Repomix Code Context — Business Requirements Specification

| | |
|---|---|
| **Status** | Draft for implementation |
| **Audience** | Any developer, including someone new to this codebase. You should not need to ask anyone anything to build this. |
| **Owner decisions still open** | See [§14 Open decisions](#14-open-decisions). Do not guess these — ask. |
| **Reads well with** | `HANDOFF.md` (repo state), `back-end/DevTeam.Broker/Gates/` (how gates work) |

---

## 0. How to use this document

1. Read §1–§4 first (why, what, vocabulary, architecture). 15 minutes.
2. Read §5 (requirements). Each one has **Given / When / Then** scenarios. Those scenarios are your tests — write them *before* the code (§11).
3. Follow §10 (build order) literally. Each step is small, shippable and reversible.
4. Before you open a PR, walk through §12 (pitfalls) and §13 (definition of done).

Words in **bold** are terms defined in §3. Anything marked ⚠️ has bitten someone (or will).

---

## 1. Problem

DevTeam runs AI agents (a Business Analyst, a Developer, QA…) against a user's project. Every time a **stage** starts, the agent begins with **no memory of the codebase**. Today it has to rediscover:

- what modules exist and how they relate,
- which shared code already exists (so it does not write a duplicate),
- the project's conventions (naming, folder layout, test style),
- the business entities and how they connect.

That rediscovery costs **tokens (money), time (the user waits), and quality** (agents that don't find the shared code re-implement it — `ReuseGate` catches this *after* the work is done, which is expensive).

We also want this knowledge to **survive reboots and new sessions**. Chat history does not.

## 2. Goal and non-goals

**Goal.** Every time a feature or hotfix is completed, DevTeam builds a compact, accurate, *committed-to-disk* description of the codebase. At the start of every later stage, the agent is handed that description first, so it starts already oriented.

**Non-goals (do not build these):**

- ❌ Making the agent read the *whole* repository every session. That is the exact problem we're solving.
- ❌ A search engine / embeddings / vector database. Out of scope.
- ❌ Keeping the map perfectly live during an in-progress feature. It is a **baseline**, not a live view (§5 REQ-006).
- ❌ Exposing the word "Repomix" (or "pack", "XML", "commit hash") to end users. See §9.
- ❌ Failing a release, hotfix or stage because indexing failed. Indexing is **best-effort** (§5 REQ-007).

## 3. Vocabulary

| Term | Meaning |
|---|---|
| **Repomix** | An external Node.js CLI that walks a repo and writes it into one AI-friendly file (XML here). It only *concatenates and (optionally) compresses* files. It does **not** understand the code. |
| **Pack** | Repomix's output: `pack.xml` (full) and `pack.compressed.xml` (signatures only — bodies stripped). Large. Never sent whole to an agent. |
| **Map** | `CODEBASE_MAP.md` — a short (target 2–5k tokens) human-readable summary of modules, shared code, conventions, entities. **This is the valuable part.** Small enough to read every stage. |
| **Structure file** | `structure.md` — facts extracted *deterministically by code* (entities, API routes, DTOs). Cannot hallucinate. Feeds the map. |
| **Slice** | The part of the codebase one feature is allowed to touch. Defined by `manifest.yaml` (`SliceManifest`). |
| **Stage / role** | One step of the pipeline (business-analyst → developer → qa). |
| **Builtin / gate** | A named C# check or step (`IGate`) that runs around an agent (e.g. `context_bundle`). |
| **Leading builtin** | A builtin listed *before* the first agent step; it runs at stage start, before the chat begins. |
| **Workspace** | The user's project folder DevTeam is operating on (not this repo). |
| **HEAD** | The workspace's current git commit. |
| **Stale** | The map was built from a commit that is not HEAD. |
| **Best-effort** | If it fails, log it and carry on. The caller must never see an exception. |

## 4. Architecture at a glance

```
 Feature/hotfix completes                       Any later stage starts
          │                                               │
          ▼                                               ▼
 FinalizeFeatureCompletionAsync            leading builtin "code_map" (new IGate)
          │  (after merge succeeded)                      │
          ▼                                               ▼
 IRepoContextService.EnqueueRefresh  ───►  reads devteam/context/current/*
          │  (non-blocking; queue)             ├─ CODEBASE_MAP.md   ──► copied into feature dir
          ▼                                    ├─ meta.json  ──► staleness check vs HEAD
 RepoContextWorker (BackgroundService)         └─ pack*.xml  ──► only a SLICE is materialised
   1. lock per workspace                                          (agent cannot read outside
   2. run repomix (timeout, size cap)                              its workspace)
   3. write to temp dir
   4. build structure.md (deterministic)
   5. (later) agent writes CODEBASE_MAP.md prose
   6. atomic swap  current/ ← temp
   7. prune old versions
```

**Two facts that shape everything:**

1. ⚠️ **The agent can only read files inside its workspace.** DevTeam scopes agent permissions to the workspace (`WorkspaceScopedPermissionPolicy`). A pack stored in DevTeam's own data directory (`RuntimeIdentity.DataDirectory`) is **invisible to the agent**. That's why the canonical store is inside the workspace (`devteam/context/`), not the data dir — see REQ-004.
2. ⚠️ **`IWorkflowEngine` is registered `AddScoped`** (`Program.cs`) and owns a per-request `DbContext`. A background worker must be a **singleton** and create its own scope. Never capture the engine's `db` in the queued work.

## 5. Requirements

Every requirement lists a **priority** (MUST / SHOULD / COULD) and **acceptance criteria** in Given/When/Then form (this is also the format DevTeam's own `gherkin_validator` gate expects, so it doubles as a worked example of a valid BRS).

---

### REQ-001 — Build a code index when work completes  · MUST

When a feature (or hotfix) finishes its pipeline and is merged, DevTeam builds a fresh index of the workspace.

**Where to hook:** `WorkflowEngine.FinalizeFeatureCompletionAsync` (`WorkflowEngine.cs`, search for the name). It is called from the four completion paths (last-stage signoff, run-gates, run-stage, retry). Hotfixes use the same method; they merge into `main` (`release.BranchName == "main"`).

**Acceptance criteria**

```gherkin
Feature: Index the code base when work completes

  Scenario: A feature finishes and merges into its release branch
    Given a feature whose pipeline has just completed
    And the merge into the release branch succeeded
    When the completion step finishes
    Then a refresh of the code index is queued for that workspace
    And the completion step returns without waiting for the index to be built

  Scenario: A hotfix finishes
    Given a hotfix that has merged into main
    When the completion step finishes
    Then a refresh is queued
    And the index is built from the merged state of main

  Scenario: The merge failed
    Given a feature whose merge into the release branch failed
    When the completion step throws
    Then no refresh is queued
```

**Example.** Feature `adding` finishes → workspace `D:\apps\calculator` is on `release/adding` at commit `a1b2c3d` → `EnqueueRefresh("D:\apps\calculator")` → the worker builds `devteam/context/a1b2c3d/…` and flips `current` to it.

---

### REQ-002 — Produce the pack with Repomix  · MUST

**Command shape** (verify flags with `repomix --help` for the installed version — ⚠️ flags evolve):

```
repomix --style xml --output <tempDir>\pack.xml \
        --ignore "<comma-separated patterns>"
repomix --style xml --compress --output <tempDir>\pack.compressed.xml \
        --ignore "<same patterns>"
```

- `--style xml` — the format agents parse best.
- `--compress` — keeps signatures/structure, drops bodies. This is your cheap "shape of the code" layer.
- Repomix runs its **security scan (Secretlint)** by default. Do **not** pass `--no-security-check`.
- Run with the workspace as the working directory. Capture stdout+stderr.

**Default ignore list** (configurable; §8): `**/node_modules/**, **/bin/**, **/obj/**, **/.next/**, **/out/**, **/dist/**, **/coverage/**, **/*.lock, **/package-lock.json, **/*.db, **/*.sqlite*, **/*.log, **/.env*, **/*.pem, **/*.pfx, **/*.key, devteam/context/**`

⚠️ `devteam/context/**` **must** be ignored, otherwise each pack contains the previous pack and grows without bound.

**Acceptance criteria**

```gherkin
Feature: Produce the pack

  Scenario: Repomix succeeds
    Given Repomix is installed
    And the workspace has source files
    When a refresh runs
    Then a full pack and a compressed pack are written
    And neither contains files matching the ignore list

  Scenario: The pack would contain its own previous output
    Given an earlier pack exists under devteam/context
    When a refresh runs
    Then the new pack does not include anything from devteam/context

  Scenario: A secret-looking file is present
    Given the workspace contains a file named ".env" with a credential in it
    When a refresh runs
    Then the credential does not appear in any pack file
```

---

### REQ-003 — Stamp every index with the commit it was built from  · MUST

Write `meta.json` next to the pack:

```json
{
  "schemaVersion": 1,
  "workspacePath": "D:\\apps\\calculator",
  "commit": "a1b2c3d4e5f6...",
  "branch": "release/adding",
  "dirty": false,
  "builtAtUtc": "2026-09-21T05:12:44Z",
  "trigger": "feature-complete",
  "repomixVersion": "1.x.y",
  "files": { "pack.xml": 1843201, "pack.compressed.xml": 402118, "CODEBASE_MAP.md": 3120 },
  "approxMapTokens": 780,
  "warnings": []
}
```

- Get the commit with `git rev-parse HEAD` (full 40 chars; short form for folder names).
- `dirty` = `git status --porcelain` is non-empty. ⚠️ A dirty tree means the pack does **not** equal the commit; record it and treat the index as *approximately* that commit.
- `approxMapTokens`: `ceil(characters / 4)` is fine. It is a guide, not a bill.

```gherkin
Feature: Commit stamp

  Scenario: A clean workspace
    Given the workspace is at commit "a1b2c3d" with no uncommitted changes
    When a refresh completes
    Then meta.json says commit "a1b2c3d" and dirty false

  Scenario: Uncommitted changes exist
    Given the workspace has uncommitted changes
    When a refresh completes
    Then meta.json says dirty true
```

---

### REQ-004 — Where the index lives  · MUST

```
<workspace>\devteam\context\
    current.json                 ← pointer: { "commit": "a1b2c3d", "dir": "a1b2c3d" }
    a1b2c3d\                     ← one folder per indexed commit
        pack.xml
        pack.compressed.xml
        structure.md
        CODEBASE_MAP.md
        meta.json
    9f8e7d6\                     ← previous version (kept; see pruning)
```

- **Inside the workspace** so the agent can read it (§4 fact 1).
- **Not committed:** DevTeam appends `devteam/context/` to **`.git/info/exclude`** (a *local-only* ignore — it does not modify the user's `.gitignore` and does not show up in their diffs). ⚠️ If you skip this, `CommitStageWorkAsync` will `git add` a multi-megabyte XML into the user's history.
- **Pruning:** keep the newest `KeepLast` (default 3) commit folders; never delete the one `current.json` points to.
- **Per-branch caveat:** a hotfix is built from `main`, a release from `release/*`. They are different commits, so they naturally get different folders. Do not use a single fixed "latest" file.

```gherkin
Feature: Storage location

  Scenario: First index for a workspace
    Given a workspace with no devteam/context folder
    When a refresh completes
    Then devteam/context/<commit>/ exists with the expected files
    And current.json points to that commit
    And .git/info/exclude contains "devteam/context/"

  Scenario: Excluding is idempotent
    Given .git/info/exclude already contains "devteam/context/"
    When a refresh completes
    Then the line appears exactly once

  Scenario: Old versions are pruned
    Given 3 older indexed commits exist and KeepLast is 3
    When a fourth refresh completes
    Then the oldest is deleted
    And the one current.json points to is kept
```

---

### REQ-005 — Refresh must be safe: atomic, serialised, non-destructive  · MUST

- **Per-workspace lock.** Two features finishing close together must not both write. Use one `SemaphoreSlim(1,1)` per normalised workspace path (store in a `ConcurrentDictionary`).
- **Coalesce.** If a refresh is already queued for a workspace and another arrives, run **one** more, not two.
- **Atomic write.** Build in `devteam/context/.tmp-<guid>/`. Only when *everything* succeeded, `Directory.Move` it to `<commit>/` and rewrite `current.json` (write `current.json.tmp` then `File.Move(..., overwrite: true)`).
- **Never delete the old good index until the new one is fully in place.** On any failure: delete the temp folder, leave `current.json` untouched.

```gherkin
Feature: Safe refresh

  Scenario: Two completions arrive close together
    Given a refresh is running for a workspace
    When two more refreshes are requested for that workspace
    Then only one additional refresh runs after the first

  Scenario: A crash halfway through
    Given a good index exists at commit "9f8e7d6"
    When a refresh fails after writing only some files
    Then current.json still points to "9f8e7d6"
    And no half-written folder is left behind

  Scenario: Different workspaces do not block each other
    Given refreshes are running for two different workspaces
    Then both make progress at the same time
```

---

### REQ-006 — Detect and handle a stale index  · MUST

At stage start compare `meta.json.commit` with the workspace `HEAD`.

| Situation | Behaviour |
|---|---|
| Same commit, clean | Use it. No banner. |
| Different commit | Use it **with a warning banner** ("built N commits ago") and queue a background refresh. **Never block the stage on a rebuild.** |
| No index at all | Skip the map (log at info). Queue a first-time refresh. Stage proceeds. |
| Index exists but unreadable/corrupt | Treat as none; queue refresh; do not throw. |

`N` = `git rev-list --count <indexed>..HEAD`. If the indexed commit no longer exists (rebased/force-pushed), say "unknown number of changes" instead of failing.

⚠️ **In-progress feature branches are always ahead of the map.** The banner text must say the map is a *baseline* and the agent should trust the actual files over the map when they disagree.

```gherkin
Feature: Staleness

  Scenario: Index matches HEAD
    Given the index was built at commit "a1b2c3d"
    And HEAD is "a1b2c3d"
    When a stage starts
    Then the map is provided without a warning

  Scenario: Index is behind
    Given the index was built at commit "a1b2c3d"
    And HEAD is 4 commits later
    When a stage starts
    Then the map is provided with a warning that it is 4 changes behind
    And a refresh is queued
    And the stage is not delayed waiting for it

  Scenario: The indexed commit no longer exists
    Given meta.json names a commit git cannot find
    When a stage starts
    Then the warning says the number of changes is unknown
    And the stage still starts
```

---

### REQ-007 — Indexing must never break real work  · MUST

Every path in the indexing feature is wrapped so **no exception escapes to the caller**.

| Failure | Required behaviour |
|---|---|
| `repomix` not installed / not on PATH | Log a warning once per refresh, record `warnings: ["repomix-missing"]`, keep old index. |
| Repomix times out (default 120 s) | Kill the **process tree**, discard temp, keep old index. |
| Output exceeds `MaxPackBytes` (default 25 MB) | Discard, keep old index, warn `pack-too-large`. |
| Non-zero exit code | Discard, keep old index, log stderr (first 2 KB). |
| Disk full / access denied | Same: discard, keep old, log. |
| Git unavailable / not a repo | Skip with a warning. |
| Cancellation (broker shutting down) | Stop cleanly; delete temp. |

```gherkin
Feature: Best-effort indexing

  Scenario: Repomix is not installed
    Given Repomix is not available on this machine
    When a feature completes
    Then the feature is still marked complete
    And a warning is logged
    And any existing index is untouched

  Scenario: Repomix hangs
    Given Repomix does not exit within the timeout
    When a refresh runs
    Then the process is killed
    And the existing index is untouched

  Scenario: The queue worker throws unexpectedly
    Given the refresh code throws an unexpected error
    When a refresh runs
    Then the error is logged
    And the worker keeps processing later requests
```

---

### REQ-008 — Build the deterministic structure file  · SHOULD (Phase 2)

`structure.md` is generated **by C# code, not by an AI**, so it is reproducible and correct. Extract:

- **Entities & relationships** from EF Core: types in `DevTeam.Broker/Domain/*.cs` with `DbSet<>` registrations and navigation properties. (Read the source with Roslyn *or* a simple regex pass — regex is acceptable for a first version; see pitfalls.)
- **API routes**: every `app.MapGet/MapPost/MapPut/MapDelete("…")` in `ApiEndpoints.cs`.
- **TypeScript DTOs**: `export interface …` in `front-end/app/Chat/Data/BrokerTypes.ts`.
- **Workflow definition**: pipeline stages and their steps from the workspace's `devteam/workflow.yaml` (if present) or the built-in default.
- **Test locations & command** already discovered by `TestDiscovery`.

Output example:

```markdown
## Entities
- DevTeamRelease 1─* ReleaseFeature 1─* ReleaseStageRun 1─* ReleaseGateCheck
- ReleaseFeature 1─1 FlowPosition; 1─* Signoff

## API routes
- POST /api/features/{featureId}/run-gates
- POST /api/features/{featureId}/run-gates-and-repair
- POST /api/releases/{releaseId}/finalize
```

> This is a **stack-specific** extractor. The user's workspace can be any stack. For non-.NET/TS workspaces, skip the extractor and rely on the compressed pack + AI prose (REQ-009). Do not pretend to extract what you can't.

```gherkin
Feature: Structure extraction

  Scenario: A workspace with EF entities
    Given a workspace with a DbContext that exposes DevTeamRelease and ReleaseFeature
    When the structure is built
    Then structure.md lists both entities and their relationship

  Scenario: A workspace in an unsupported stack
    Given a workspace with no .NET or TypeScript sources
    When the structure is built
    Then structure.md says "no automatic structure available"
    And the refresh still succeeds
```

---

### REQ-009 — An agent writes the human-readable map  · SHOULD (Phase 3)

After the pack and structure exist, DevTeam asks a model to write `CODEBASE_MAP.md` **from the compressed pack + structure.md** (not the full pack — cost).

**Required sections and size:** ≤ ~5,000 tokens total.

```markdown
<!-- generated from commit a1b2c3d on 2026-09-21 — verify against real files before relying on it -->
# Codebase map

## What this project is        (3–5 sentences, business terms)
## Modules                     (one line each: name — purpose — where)
## Shared code — REUSE THESE   (name — what it does — path)
## Conventions                 (naming, folders, test style, error handling)
## Do / Don't                  (concrete, e.g. "Don't add a second HTTP client; use ApiClient")
## Business entities           (from structure.md, in plain words)
## Known sharp edges           (things that surprised past developers)

<!-- USER NOTES (preserved on regeneration) -->
```

- **User-editable region.** Everything below the `USER NOTES` marker is copied verbatim into every regeneration. Users correct the map there.
- **Incremental, later:** on a small diff, ask the model to *update* the changed modules only, given the old map + `git diff --stat` + changed files. Fall back to a full rewrite if the diff touches > 30% of files or the map is missing.
- **Untrusted output.** Treat the model's text as data. Enforce a max size; strip anything that looks like an instruction to the next agent ("ignore previous…").

```gherkin
Feature: The map

  Scenario: A map is produced
    Given a completed pack and structure file
    When the map is generated
    Then CODEBASE_MAP.md exists
    And it is no more than 5000 tokens
    And it names the commit it was built from

  Scenario: The user has added their own notes
    Given the existing map has text below the USER NOTES marker
    When the map is regenerated
    Then that text is preserved unchanged

  Scenario: The model is unavailable
    Given the model provider rejects the request
    When the map is generated
    Then the pack and structure are still saved
    And the map is marked "not generated yet"
    And the refresh does not fail
```

---

### REQ-010 — Give the map to each stage  · MUST (after REQ-006 and REQ-009, or with a structure-only map)

Add a **leading builtin `code_map`** (new `IGate`), placed **before `context_bundle`** in each role's `steps`. It:

1. Reads `devteam/context/current.json`; if missing → passes with "no overview yet" (never fails the stage).
2. Checks staleness (REQ-006).
3. Writes `devteam/features/<featureKey>/codemap.md` = the map + staleness banner.
4. Optionally writes `devteam/features/<featureKey>/codeslice.xml`: only the **pack entries whose path is under the feature's slice** (`SliceManifest`: `CodePathBack`, `CodePathFront`, `Shared`) — this is how "fetch only what a stage needs" is done given the agent can't fetch on demand from outside its workspace.
5. Returns `GateResult.Pass` always (it is informational), with evidence like `map from a1b2c3d (4 changes behind)`.

`context_bundle` already concatenates BRS + working areas + shared-core hints into `context.md`. **Do not edit `ContextBundleGate`.** Instead, the new gate writes its own file and the stage prompt tells the agent to read both (see `BuildArtifactContext` in `WorkflowEngine.cs` for how artifact files are referenced — extend by *adding* a new helper, not editing the existing one).

```gherkin
Feature: Provide the map to a stage

  Scenario: A fresh map exists
    Given an index at the same commit as HEAD
    When the developer stage starts
    Then codemap.md is written into the feature folder
    And the stage prompt tells the agent to read it first

  Scenario: The full pack is never injected wholesale
    Given a pack of 2 MB exists
    When any stage starts
    Then the prompt contains at most the map and the slice, not the whole pack

  Scenario: No index exists yet
    Given no devteam/context folder
    When a stage starts
    Then the stage starts normally
    And the check reports "no project overview yet"
```

---

### REQ-011 — Status and manual control for the user  · SHOULD

- `GET /api/workspaces/code-context?workspacePath=…` → `{ state, changesBehind, builtAt, warnings[] }`
  `state` ∈ `upToDate | behind | refreshing | unavailable | none`.
- `POST /api/workspaces/code-context/refresh?workspacePath=…` → queues a refresh; returns `202`.
- UI shows a small status line (see §9 for the exact wording) with a "Refresh now" button.

```gherkin
Feature: Status for the user

  Scenario: The index is up to date
    Given the index matches HEAD
    When the user opens the project
    Then they see "Project overview: up to date"

  Scenario: Repomix is missing
    Given Repomix is not installed
    When the user opens the project
    Then they see "Project overview: unavailable" with a one-line fix
    And no technical jargon is shown
```

---

### REQ-012 — Configuration  · SHOULD

`appsettings.json` section `RepoContext` (defaults shown):

```json
"RepoContext": {
  "Enabled": true,
  "RepomixCommand": "repomix",
  "TimeoutSeconds": 120,
  "MaxPackBytes": 26214400,
  "KeepLast": 3,
  "ExtraIgnorePatterns": [],
  "GenerateMapWithModel": true
}
```

`Enabled: false` must make every entry point a no-op (useful in tests and for users who don't want it).

### REQ-013 — Privacy and secrets  · MUST

- Repomix's Secretlint scan stays **on**.
- ⚠️ **Read this carefully:** Repomix's default behaviour on a detected secret is to **exclude that file and report it** — it does **not** fail. If you want "a secret hit blocks writing the pack", you must parse Repomix's output/exit code yourself (verify the behaviour on the installed version) and treat any reported suspicious file as `warnings: ["secret-suspected:<path>"]`, then decide: (a) keep the pack (the file is already excluded) but surface the warning, or (b) discard. **Recommended: (a) + warning + also apply DevTeam's own deny-list *before* running Repomix.** Don't rely on one layer.
- The map (and slice) is sent to a model provider as part of prompts. Say so in user docs. Never put file contents matching the deny-list into the map prompt.
- Never log pack contents. Log sizes and paths only.

```gherkin
Feature: Secrets

  Scenario: A private key file exists
    Given the workspace contains "server.pem"
    When a refresh runs
    Then the key material is not in any output file

  Scenario: Repomix reports a suspected secret
    Given Repomix reports it excluded a file as suspicious
    When the refresh completes
    Then meta.json has a warning naming that file
    And the warning does not include the secret itself
```

### REQ-014 — Observability  · SHOULD

Structured log line per refresh: `workspace, commit, trigger, durationMs, packBytes, mapTokens, outcome, warnings`. One line at start, one at end. No file contents. Broadcast a `codeContextChanged` event via the existing `IEventBroadcaster` so the UI can update without polling.

---

## 6. Data contracts

### `current.json`
```json
{ "commit": "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0", "dir": "a1b2c3d", "updatedAtUtc": "2026-09-21T05:12:44Z" }
```

### C# surface (suggested — match the repo's style: file-scoped namespaces, `sealed`, primary ctors, `CancellationToken ct` last)

```csharp
namespace DevTeam.Broker.Context;

public enum RepoContextState { None, UpToDate, Behind, Refreshing, Unavailable }

public sealed record RepoContextStatus(
    RepoContextState State, int? ChangesBehind, DateTimeOffset? BuiltAt, IReadOnlyList<string> Warnings);

public interface IRepoContextService
{
    /// Queue a refresh. Never throws, never blocks on the build.
    void EnqueueRefresh(string workspacePath, string trigger);

    Task<RepoContextStatus> GetStatusAsync(string workspacePath, CancellationToken ct);
}

/// Thin wrapper over IProcessRunner (Gates/GateModels.cs) so Repomix is fakeable in tests.
public interface IRepomixRunner
{
    Task<RepomixResult> RunAsync(string workspacePath, string outputPath, bool compress,
                                 IReadOnlyList<string> ignore, TimeSpan timeout, CancellationToken ct);
}
```

Reuse what exists: `IProcessRunner` / `ProcessRunRequest(FileName, Arguments, WorkingDirectory, TimeoutMs)` / `ProcessRunResult(… TimedOut …)`; `ArtifactPaths` for feature paths; `GateResult.Pass/Fail`; `IGitService` for git.

## 7. Files you will touch (and how)

> **Team rule (from the project's engineering standards): composition over modification.** Add new classes and new methods. Do **not** edit the body of an existing working method. Where a change forces touching an existing file, it is listed below with the *smallest possible* edit.

| File | Change | Kind |
|---|---|---|
| `DevTeam.Broker/Context/IRepoContextService.cs` | new | additive |
| `DevTeam.Broker/Context/RepoContextService.cs` | new (queue + worker logic) | additive |
| `DevTeam.Broker/Context/RepoContextWorker.cs` | new `BackgroundService` | additive |
| `DevTeam.Broker/Context/RepomixRunner.cs` | new | additive |
| `DevTeam.Broker/Context/CodeStructureExtractor.cs` | new (Phase 2) | additive |
| `DevTeam.Broker/Context/CodeMapWriter.cs` | new (Phase 3) | additive |
| `DevTeam.Broker/Gates/CodeMapGate.cs` | new `IGate` | additive |
| `DevTeam.Broker/Workflow/GateFriendlyText.cs` | add a title for `code_map` (one dictionary entry) | additive |
| `DevTeam.Broker/Workflow/BuiltinRegistry.cs` | add `CodeMap` constant **and** add it to `All` | ⚠️ edits a property body — see note |
| `DevTeam.Broker/Gates/GateRunner.cs` | register the new gate in `Create(...)` | ⚠️ one line |
| `DevTeam.Broker/Program.cs` | register services + hosted service | additive lines |
| `DevTeam.Broker/Workflow/WorkflowEngine.cs` | **4 call sites** of `FinalizeFeatureCompletionAsync` → a new wrapper (see below) | ⚠️ needs owner OK |
| `DevTeam.Broker/Workflow/WorkflowYaml.cs` | add `code_map` to default pipeline steps | ⚠️ changes defaults; updates tests |
| `DevTeam.Broker/ApiEndpoints.cs` | two new endpoints | additive |
| `DevTeam.Broker/Git/IGitService` + `GitService.cs` | add `GetHeadAsync`, `IsDirtyAsync`, `CountCommitsAsync` | additive — ⚠️ but every `IGitService` fake in tests must get them (`FakeGitService` in `WorkflowEngineTests.cs`) |
| front-end `BrokerApi.ts`, `BrokerTypes.ts`, a small status component | additive | additive |

**How to add the hook without editing a method body.** Add a *new* private method:

```csharp
private async Task FinalizeFeatureCompletionAndIndexAsync(DevTeamDbContext db, ReleaseFeature feature, CancellationToken ct)
{
    await FinalizeFeatureCompletionAsync(db, feature, ct);          // untouched original
    _repoContext.EnqueueRefresh(feature.Release.WorkspacePath, feature.Release.IsHotfix ? "hotfix-complete" : "feature-complete");
}
```

…then change the **four call sites** to call it. That is four one-token edits at call sites, not a rewrite of any method body — but it *is* still an edit to `WorkflowEngine`, so **get the owner's explicit OK** (§14, decision 1). It also needs a new constructor parameter (`IRepoContextService`), which changes every place `new WorkflowEngine(...)` is called in tests (there are several — search `new WorkflowEngine(`).

**About `BuiltinRegistry.All`.** It is a property that builds a new `HashSet` each call. Adding an entry means editing that body. The safest way: add the constant, and add one line to the set. Then add `code_map` to the test lists that enumerate builtins (`GateFriendlyTextTests.EveryKnownCheck_HasAJargonFreeTitle`).

## 8. Configuration and defaults — summary table

| Setting | Default | Why |
|---|---|---|
| Timeout | 120 s | Big repos can take a minute; a hang must not hold the worker forever. |
| Max pack | 25 MB | Beyond this the pack is not useful and risks memory pressure. |
| Keep last | 3 | Rollback + debugging without unbounded disk use. |
| Queue capacity | bounded, drop-oldest per workspace | A burst of completions shouldn't grow memory. |

## 9. Words the user sees (no jargon)

The end user is a **beginner / non-technical**. They must never see: *Repomix, pack, XML, commit, HEAD, stale, index, token, Secretlint.*

| Internal | Show the user |
|---|---|
| `state = upToDate` | **Project overview: up to date** |
| `state = behind` | **Project overview: a few changes behind — updating in the background** |
| `state = refreshing` | **Project overview: updating…** |
| `state = none` | **Project overview: not created yet — it will be made after your first finished feature** |
| `state = unavailable` (Repomix missing) | **Project overview: unavailable. A helper tool it needs isn't installed. [How to fix]** |
| `pack-too-large` | **Your project is very large, so the overview was skipped.** |
| `secret-suspected:<path>` | **A file that may contain a password was left out of the overview.** |
| button | **Update overview now** |

Agent-facing text (the map, the staleness banner) *can* be technical — it's for the model — but keep the banner plain: `This overview was made 4 changes ago. If it disagrees with the real files, trust the files.`

Also add the new builtin to `GateFriendlyText` so a failure never says `code_map`: title **"Reading the project overview"**.

## 10. Build order (each step ends green and shippable)

| # | Step | Ships value? | Risk |
|---|---|---|---|
| 1 | `IRepomixRunner` + `RepomixRunner` with fakeable `IProcessRunner`; detect missing/timeout | no (plumbing) | low |
| 2 | `RepoContextService.RefreshAsync`: temp dir → run → write `meta.json` → atomic swap → prune → `.git/info/exclude` | **yes** (index exists on disk) | low |
| 3 | Worker + queue + per-workspace lock + coalescing | yes | medium (concurrency) |
| 4 | Wrapper + 4 call sites + DI (needs §14 decision 1) | **yes** (automatic on completion) | medium (touches engine) |
| 5 | Git helpers + staleness (`GetStatusAsync`) | yes | low |
| 6 | Status endpoint + UI line | yes (visibility) | low |
| 7 | `CodeStructureExtractor` → `structure.md` | yes | medium (parsing) |
| 8 | `CodeMapWriter` (model → `CODEBASE_MAP.md`, user-notes preserved) | **yes** (the real value) | medium (cost, model failures) |
| 9 | `CodeMapGate` + slice materialisation + prompt wording | **yes** (agents benefit) | **highest** — changes what every stage sees |

Ship 1–6 first. **Step 9 last**, behind `Enabled`, and dogfood it on a scratch workspace before turning it on by default.

## 11. Test plan (write these FIRST — the project rule is test-first)

Framework: **xUnit** for backend (`DevTeam.Tests`), **Jest + React Testing Library** for the frontend. Follow `DevTeam.Tests/GateSelfHealerTests.cs` as a style reference (in-memory SQLite, fakes for the runner/coordinator).

**Fakes you need:** `FakeRepomixRunner` (returns canned files/exit codes, can throw, can hang until cancelled, can report `Missing`), `FakeGitService` additions, a temp-directory helper (see `TempDir` in `WorkflowEngineTests.cs`).

Minimum tests (map them 1:1 to the scenarios above):

- **Runner:** missing tool → `Missing`; timeout → `TimedOut` and process killed; non-zero exit → failure with truncated stderr.
- **Refresh:** success writes all files; `meta.json` correct; `current.json` flips **only after** success; failure leaves old index; temp folder removed; ignore list respected; `devteam/context/**` excluded; `.git/info/exclude` idempotent.
- **Prune:** keeps newest N, never deletes current.
- **Concurrency:** two rapid requests → one extra run; two workspaces run in parallel; cancellation cleans temp.
- **Staleness:** same/behind/missing-commit/no-index/corrupt-`meta.json`.
- **Hook:** feature completion queues a refresh; failed merge doesn't; hotfix uses trigger `hotfix-complete`; **a throwing `IRepoContextService` does not fail completion**; `Enabled=false` is a no-op.
- **Gate:** always `Pass`; writes `codemap.md` with banner when behind; no index → pass with message; slice contains only allowed paths; full pack never in prompt.
- **Friendly text:** `code_map` has a jargon-free title; user-facing strings contain none of the banned words (§9) — a single test that scans the strings.
- **API:** status shape; refresh returns 202; unknown workspace → sensible 404/empty.
- **Frontend:** each `state` renders the exact §9 wording; refresh button calls the API; no banned words appear.

**Definition of "tests done":** they were written first, failed for the right reason, then passed; the **entire** suite passes.

⚠️ **Existing red tests:** four tests in `WorkflowResilienceTests` currently fail independent of this work. Don't "fix" them as part of this change and don't let them hide a new failure — compare the failing list before and after.

## 12. Pitfalls (read before you code)

1. **Agent can't read outside the workspace.** (§4.) The reason the store is `devteam/context/`. Don't move it to the data dir.
2. **Scoped DI vs singleton worker.** `IWorkflowEngine` is scoped. The worker must be singleton and use `IServiceScopeFactory` if it needs the DB. Don't capture `db`/`feature` entities in the queued work — copy the strings you need (`workspacePath`, trigger).
3. **Windows launches `.cmd` shims badly.** `repomix` installed by npm is `repomix.cmd`. `Process.Start` with `UseShellExecute=false` can't execute a `.cmd` directly by bare name. Either resolve the real path and run via `cmd.exe /c`, or use `node <path-to-repomix-js>`. Test on Windows — this repo is developed there. Check what `SystemProcessRunner` does before assuming.
4. **Quoting.** Workspace paths contain spaces. Build arguments carefully; prefer an argument list if `ProcessRunRequest` allows, otherwise quote every path.
5. **Kill the process *tree* on timeout.** Repomix (Node) may spawn children. Killing only the parent leaves orphans. (See how `SystemProcessRunner` and `WorkspaceProcessCleanupService` do it; also see the warning in `HANDOFF.md` about **never killing processes by name** — the harness itself is an `opencode.exe`. Kill by PID only.)
6. **Pack contains its own previous output** → unbounded growth. Ignore `devteam/context/**`.
7. **Committing the pack into the user's repo.** Use `.git/info/exclude`, not `.gitignore`. `CommitStageWorkAsync` does `git add` of workspace changes.
8. **A dirty tree is not a commit.** Record `dirty`. Don't claim the pack "is" commit X when there are uncommitted edits.
9. **Which branch is checked out?** After `FinalizeFeatureCompletionAsync` the workspace is on the release branch (or `main` for hotfixes). Index *then*, not before the merge — the pack must include the merged code. If a later step switches branches while a refresh is running, the pack can be built from the wrong tree. Read `HEAD` **before and after** the Repomix run; if they differ, discard and re-queue.
10. **Adding a method to `IGitService` breaks every fake.** Search `: IGitService` and update them all.
11. **Adding a constructor parameter to `WorkflowEngine` breaks every `new WorkflowEngine(...)`** in tests. Update them, or add an overload — check first how many there are.
12. **Adding a step to the default pipeline breaks tests that list steps** (e.g. the `PIPELINE` fixture in `ReleaseWizard.test.tsx`, loader tests, `PipelineEditorServiceTests`). Update them deliberately and *say so in the PR*.
13. **Don't put the model call inside the completion path.** It can take a minute and can fail (e.g. provider rejected — see the `RpcException` note in §15). It belongs in the background worker.
14. **Model output is untrusted.** It becomes part of *every* future prompt. Cap its size, strip instruction-like lines, and never let it overwrite the user-notes region.
15. **Token estimates are estimates.** Don't fail a refresh because the estimate says 5,300 vs 5,000; warn.
16. **Regex extraction of C# is brittle** (generics, partial classes, attributes on multiple lines). Fine for v1 *if* you clearly label the output as "approximate" and unit-test the awkward cases. Prefer Roslyn (`Microsoft.CodeAnalysis.CSharp`) if you'll extend it.
17. **Line endings.** The repo has CRLF/LF mixing (git warns). When writing/preserving the user-notes region, normalise both ways or you'll create diffs every regeneration.
18. **Long path / reserved names on Windows.** Keep folder names to the short commit (7–12 chars), not the full 40.
19. **Clock/ordering.** Don't sort versions by folder mtime. Use `meta.json.builtAtUtc`.
20. **Idempotence.** Running the same refresh twice at the same commit should be cheap: if `<commit>/meta.json` already exists and is valid, just repoint `current.json` and return.

## 13. Best practices & Definition of Done

**Best practices**

- Small classes, one job each; inject `TimeProvider`/clock and `IFileSystemService`-style seams so tests don't sleep or touch the real disk unnecessarily.
- Log **decisions** ("skipping: repomix missing") not chatter.
- Prefer returning result objects (`RefreshResult { Outcome, Warnings }`) over throwing internally; the *only* place you catch-all is the worker loop.
- Every `catch (Exception)` must be justified by REQ-007 and **rethrow `OperationCanceledException`**.
- Comments explain *why* (the repo's existing comments are a good model), not what.
- Keep user-facing strings in one place (like `labels.ts` / `GateFriendlyText`) so the jargon test can scan them.

**Definition of done** — all true:

- [ ] Tests written first; new tests fail before the code and pass after.
- [ ] Whole backend suite (`dotnet test`) and frontend suite (`npx jest`) green, except any tests that were *already* failing and are listed in the PR.
- [ ] A feature completing with Repomix **uninstalled** still completes (manually verified once).
- [ ] A hung Repomix is killed (manually verified once) and no orphan `node.exe` remains.
- [ ] `.git/info/exclude` updated; `git status` in the workspace is clean after a refresh.
- [ ] No jargon in any user-visible string (§9); the scan test passes.
- [ ] `Enabled: false` proven to be a no-op.
- [ ] Existing methods' bodies unchanged except the sanctioned call sites (§7); reviewer can see that in the diff.
- [ ] Secrets test passes with a real `.env` and `.pem` in a scratch workspace.
- [ ] PR description lists: decisions taken from §14, any changed defaults, any tests updated and why.

## 14. Open decisions

Ask the owner; don't guess.

1. **Call-site edits in `WorkflowEngine` (4 places).** Sanctioned exception to "never edit existing method bodies"? *Alternative:* a hosted service that watches release/feature status changes and refreshes — no engine edit, but eventual and needs a change signal (the engine already broadcasts `stageStateChanged`; a subscriber to `IEventBroadcaster` may work without touching the engine — investigate first, it may be cleaner).
2. **Where the pack lives:** `devteam/context/` in the workspace (this doc's choice) vs DevTeam's data dir (agent can't read it; DevTeam would have to materialise slices into the workspace anyway).
3. **Secret found → keep pack with the file excluded (recommended) or discard the whole pack?**
4. **Who pays for the map?** Model call per completion. Default model? Cap per day?
5. **Default pipeline change:** should `code_map` be on by default, or opt-in per workspace at first?
6. **Non-.NET/TS workspaces:** ship v1 with compressed-pack-only (no structure extraction) for them?
7. **Should the map be visible/editable in the UI**, or only via the file?

## 15. Related: the `RpcException` from the provider

While preparing this, the broker threw `RpcException: Internal error: Error from provider (Console): OpenCode's free tier can only be used from within OpenCode` from `PromptWithDelegationAsync` under `RunStageAsync`. That is **the model provider rejecting the request**, not a bug in the prompt. It matters here because **REQ-009 makes a model call** — it will hit the same wall if the configured model is that free-tier one. So: (a) the map writer must treat provider rejection as an expected, non-fatal outcome (REQ-009 scenario 3), and (b) the user needs a clear message ("the AI provider refused the request — check its settings") rather than a stack trace. See `StageErrorKind.ProviderRejected` for the existing classification.

## 16. Worked example, end to end

*Workspace* `D:\apps\calculator`, feature `adding` done and merged to `release/adding` (commit `a1b2c3d`).

1. `FinalizeFeatureCompletionAndIndexAsync` runs. Merge OK → `EnqueueRefresh("D:\apps\calculator","feature-complete")` returns immediately. The user sees "Feature complete".
2. Worker takes the workspace lock, creates `devteam\context\.tmp-7f3a\`.
3. Runs Repomix twice (full + `--compress`), 4 s. Reads HEAD before (`a1b2c3d`) and after (`a1b2c3d`) — same, continue.
4. Builds `structure.md`, asks the model for `CODEBASE_MAP.md` (≈ 900 tokens), writes `meta.json`.
5. `Directory.Move(.tmp-7f3a → a1b2c3d)`; `current.json` → `a1b2c3d`; prune older than the newest 3.
6. Next day the user starts feature `subtracting` (HEAD is now `e5f6a7b`, 2 commits later). Stage starts → `code_map` gate: index `a1b2c3d`, HEAD `e5f6a7b`, behind by 2 → writes `codemap.md` with banner *"made 2 changes ago — trust the files if they disagree"*, queues a refresh, returns Pass. The BA agent starts already knowing there's an `Adder` module to extend rather than duplicate.
7. UI shows **Project overview: a few changes behind — updating in the background**.

**Failure variant:** step 3 times out. The process tree is killed, `.tmp-7f3a` deleted, `current.json` still `9f8e7d6` (older but valid), warning logged. The user's feature was already marked complete in step 1 — they never noticed.

## 17. FAQ

**Why not just let the agent run Repomix itself?** Costs tokens and time every stage, and the agent may not have the tool or permission. Doing it once at completion is the whole point.

**Why not embed the whole pack in the prompt?** Hundreds of thousands of tokens. That's the problem we're solving.

**Why XML?** Repomix's XML output has explicit file boundaries models handle reliably. The *map* is Markdown because it's smaller and a human can edit it.

**Why the commit hash?** It's the only cheap, exact way to know "is this description still true?".

**Can the map be wrong?** Yes. Hence the header ("verify before relying"), the staleness banner, the user-notes region, and the rule that real files win over the map.

**What if two people use one workspace?** Out of scope; the per-workspace lock keeps our own writes safe.

## 18. Glossary of paths

| Path | Purpose |
|---|---|
| `devteam/context/current.json` | Pointer to the active index |
| `devteam/context/<commit>/pack.xml` | Full pack (big) |
| `devteam/context/<commit>/pack.compressed.xml` | Signatures-only pack |
| `devteam/context/<commit>/structure.md` | Deterministic facts |
| `devteam/context/<commit>/CODEBASE_MAP.md` | The short map (the valuable bit) |
| `devteam/context/<commit>/meta.json` | Stamp: commit, dirty, sizes, warnings |
| `devteam/features/<key>/codemap.md` | Map copy + banner, written by `code_map` for that stage |
| `devteam/features/<key>/codeslice.xml` | Pack entries for the feature's slice only |
| `.git/info/exclude` | Local ignore so none of the above is committed |
