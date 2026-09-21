# Project Navigation Redesign — Business Requirements Specification

| | |
|---|---|
| **Status** | Approved direction; ready to build |
| **Audience** | Any developer, including someone new to this codebase. You should not need to ask anyone anything to build this. |
| **Replaces** | The single "release → giant detail page" UI in `front-end/app/Project/Release/ReleaseWizard.tsx` |
| **Related** | `REPOMIX_BRS.md` (separate feature), `HANDOFF.md` (repo state) |
| **Open decisions** | §14 — each has a safe default; the build does not need to wait on them |

---

## 0. How to use this document

1. Read §1–§4 (problem, goals, vocabulary, navigation model). 20 minutes. §1 contains the **root cause of a real bug** — understand it before touching anything.
2. Read §5 (requirements). Each has **Given / When / Then** scenarios. Those scenarios are your tests — write them *before* the code (§11).
3. Follow §10 (build order) literally. Phase 0 is a small bug fix that ships on its own.
4. Before opening a PR, walk §12 (pitfalls) and §13 (definition of done).

⚠️ marks something that has already bitten someone, or will.

---

## 1. Problem

### 1.1 What the user sees

The user opens their project `D:\apps\calculator`. A release ("adding") exists and its feature `adding` is finished. They want to start a second feature, `subtraction`. **The UI gives them no working way to do it**, and the page keeps saying the work is finished.

### 1.2 Root cause (verified in code)

1. When a feature completes, `WorkflowEngine.FinalizeFeatureCompletionAsync` sets the **release** to `Ready` (`WorkflowEngine.cs`, `release.Status = ReleaseStatus.Ready;`).
2. `WorkflowEngine.CreateFeatureAsync` **never resets** that status. It creates the new feature (`InProgress`), checks it out, and returns — the release stays `Ready`.
3. `StageScreen` (in `ReleaseWizard.tsx`) begins with:
   `if (release.status === "Ready" || release.status === "Complete" || !role) return <"Release complete. All stages finished successfully.">`
   So although `subtraction` is in progress, the screen shows "Release complete" and **never renders its workflow**.
4. Compounding it: the only "Add another feature" control is inside a collapsed `<details>` at the very bottom of a long page, below a "Release complete" banner.

**Root of the root:** `Ready` is *stored* on the release, but it is really a *derived* fact — "every feature in this release is complete". Storing it lets it go stale the moment a feature is added, and `FinalizeFeatureCompletionAsync` even sets it when *other* features are still open.

### 1.3 Structural problems behind it

| # | Problem | Evidence |
|---|---|---|
| A | The UI is one page centred on a **"current feature"**, a concept that comes from which **git branch is checked out** (`WorkspaceActiveCheckout`). Git internals leak into the UX. | `DevTeamRelease.CurrentFeatureId` is `[NotMapped]`, populated from `WorkspaceActiveCheckout` in `PopulateCurrentFeatureIdAsync` |
| B | **Viewing a feature = switching to it** (`switchFeatureAsync`: checks out the branch, stashes work-in-progress). Completed features cannot even be switched to (`canSwitch = … f.status !== "Complete"`), so their history/logs are unreachable. | `FeatureList` in `ReleaseWizard.tsx` |
| C | Release-level convenience fields (`release.stageRuns`, `.signoffs`, `.flowPosition`) are **proxies of the current feature only**. Everything in `ReleaseDetail`/`StageScreen` reads them, so it can only ever show one feature. | `DevTeamRelease.StageRuns => CurrentFeature?.StageRuns ?? []` |
| D | Navigation state is a bare `useState("list" \| "create" \| "detail")`. Refresh the browser and you lose your place; no deep links; back button does nothing. | `ReleaseWizard()` |
| E | A 2,149-line file holds everything. Hard to change safely. | `wc -l ReleaseWizard.tsx` |

### 1.4 Good news that shrinks the work

The release payload **already contains every feature's data**: `LoadReleaseAsync` includes `Features → StageRuns → GateChecks/Findings/GuidanceNotes/SpecialistConsultations`, `Signoffs` and `FlowPosition`, and `ReleaseFeatureDto` already has optional `stageRuns`, `signoffs`, `flowPosition`. So **no new backend endpoint is required to view a non-current feature.** A pure function can re-point the existing stage screens at any feature (§6.2). Chat messages and artifacts are already fetched per `featureId`/`stageRunId`.

## 2. Goal and non-goals

**Goal.** Navigation that matches how people already think about files:

```
Project  →  Release / Urgent-fix "folders"  →  Features  →  the feature's workflow
```

- Opening the project shows all **open** releases and hotfixes as distinguishable folders (shipped ones tucked away).
- Opening a folder shows its features, with plain-language status and "needs you" signals, and an obvious **＋ New feature**.
- Opening a feature opens its workflow **at the right stage** — for finished features too, as a **read-only** record (history, logs, files).
- **Looking never changes anything.** Only an explicit "Resume work here" action touches git.

**Non-goals (do not build):**

- ❌ File-manager behaviours: rename, delete, drag-and-drop.
- ❌ Rewriting the stage screens (`StageScreen`, `ChatStage`, `StageLog`, panes). They work; **reuse them**.
- ❌ New backend endpoints for viewing (see §1.4). Backend changes are limited to Phase 0.
- ❌ Changing pipeline/gate/agent behaviour.
- ❌ Exposing git words (checkout, stash, branch, HEAD) to end users.

## 3. Vocabulary

| Term | Meaning |
|---|---|
| **Project** | The user's folder (`workspacePath`), e.g. `D:\apps\calculator`. |
| **Release** | A container of features that ship together (`DevTeamRelease`, `isHotfix=false`). Shown as a **folder**. |
| **Hotfix / Urgent fix** | A one-feature release-shell branched from `main` (`isHotfix=true`). Shown as a **red folder**. |
| **Feature** | One piece of work with its own branch and pipeline (`ReleaseFeature`). |
| **Stage** | A pipeline step (business-analyst → developer → qa). |
| **Active feature** | The single feature per **project** whose branch is currently checked out (`WorkspaceActiveCheckout.ActiveReleaseFeatureId`). ⚠️ One per *project*, not per release. |
| **Paused** | A feature that is not the active one (`OnHold`, or in progress but not checked out). Its workflow can be *viewed* but not *driven*. |
| **Read-only view** | Everything visible, no action buttons that would run the agent or change state. |
| **Effective status** | A release's status computed from its features (§5 REQ-001), replacing the stale stored value for display. |
| **Attention badge** | A "needs you" marker on a card (approval waiting, checks failed, AI model problem). |
| **Adapter** | `releaseForFeature(release, featureId)` — pure function that returns a copy of the release whose "current feature" proxies point at any chosen feature (§6.2). |

### 3.1 Words the user sees (central map — `terms.ts`)

All visible nouns live in one file so wording can change without hunting through components.

| Internal | Shown to user (default) |
|---|---|
| Release | **Release** |
| Hotfix | **Urgent fix** |
| Feature | **Feature** |
| `OnHold` | **Paused** |
| `Proposed` | **Not started** |
| `InProgress` (feature) | **In progress** |
| `Complete` (feature) | **Done** |
| Release `Ready` (all features done) | **Ready to ship** |
| Release `Released` | **Shipped** |
| Checkout / switch | **Resume work here** |
| `BlockedSignoff` | **Waiting for your approval** |
| `BlockedGate` | **Some checks didn't pass** |
| `Escalated` + `ProviderRejected` | **The AI service needs attention** |

(The existing `labels.ts` already has stage/phase/status labels — **extend it, don't duplicate it**. `terms.ts` only adds the new nouns.)

## 4. The navigation model

Three levels, one breadcrumb, hash-based URLs.

```
Level 0  PROJECT HOME            #/
   ├─ Open releases & urgent fixes as folders
   ├─ ＋ New release   ·   ＋ Urgent fix
   └─ ▸ Shipped (collapsed)

Level 1  RELEASE FOLDER          #/r/<releaseId>
   ├─ Feature cards (plain-language status, stage progress, attention badge)
   ├─ ＋ New feature (always visible)
   └─ Ship this release (only when every feature is Done)

Level 2  FEATURE VIEW            #/r/<releaseId>/f/<featureId>
   ├─ Stepper (each stage clickable → read-only peek)      #/…/s/<stageName>
   ├─ Banner if the feature is Paused / Done
   └─ The existing stage screen, re-pointed at this feature
```

Breadcrumb: `Calculator › Release "adding" › subtraction › Developer`. Every crumb is a link.

**Hotfix shortcut:** a hotfix folder has exactly one feature, so `#/r/<hotfixReleaseId>` **redirects** to its feature view (breadcrumb still shows the folder).

### 4.1 Why hash routes (`#/…`) and not `/path/…`

`next.config.js` uses `output: "export"` (static site) in production and `rewrites` in dev, and `app/` has a single `page.tsx`. Real path routes would 404 in `next dev` and rely on the broker's SPA fallback in production. **Hash routes work identically in dev, production and tests**, need no server support, and survive refresh. ⚠️ Don't add Next.js dynamic routes.

### 4.2 Two tabs, one page

`App.tsx` renders `ReleaseWizard` inside `<div hidden={activeTab !== "releases"}>` — it stays mounted while you visit Git/Settings. Route state must therefore be **read from the URL hash**, and the hash must only be *written* while the Releases tab is active, otherwise Settings would clobber it. See pitfall 6.

## 5. Requirements

Priority: MUST / SHOULD / COULD. Acceptance criteria use Given/When/Then (also the format DevTeam's own requirements gate expects, so this doubles as a worked example).

---

### REQ-001 — A release's status must be derived from its features  · MUST · Phase 0

**Rule** (`ReleaseStatusRules.Effective`):

| Stored status | Features | Effective status |
|---|---|---|
| `Released`, `Cancelled`, `Draft` | any | unchanged |
| `Ready` | every feature `Complete` (and ≥ 1 feature) | `Ready` |
| `Ready` | **any** feature not `Complete` | `InProgress` |
| any other | any | unchanged |

Expose it as a **new read-only, non-persisted** property `DevTeamRelease.EffectiveStatus` (`[NotMapped]`, get-only) so nothing about storage changes. The frontend then uses `effectiveStatus ?? status`.

**Do not** change `FinalizeFeatureCompletionAsync` or `CreateFeatureAsync` (composition rule; also see pitfall 1).

```gherkin
Feature: Release status follows its features

  Scenario: A new feature is added to a release whose only feature was done
    Given a release stored as "Ready" with one Complete feature
    When a second feature is added and is in progress
    Then the release's effective status is "InProgress"

  Scenario: All features are done
    Given a release stored as "Ready" whose features are all Complete
    Then the effective status is "Ready"

  Scenario: One feature done, another paused
    Given a release stored as "Ready", one feature Complete and one OnHold
    Then the effective status is "InProgress"

  Scenario: A shipped release
    Given a release stored as "Released"
    Then the effective status is "Released" regardless of its features

  Scenario: The stored value is untouched
    Given any release
    When effective status is read
    Then the value saved in the database is unchanged
```

**Frontend part:** `StageScreen`'s early return must use the effective status, so the reported bug is fixed even before the redesign lands. This is the **one sanctioned edit to an existing component** in Phase 0 (a one-token change, covered by a test that reproduces "adding done → add subtraction").

---

### REQ-002 — Project home shows folders  · MUST · Phase 2

Shows every **open** release and hotfix for the project as a folder card.

Card content: icon (📁 release / 🔥 urgent fix), title, plain-language state (§3.1), **progress** ("2 of 3 features done"), **last activity** ("2 hours ago"), and an attention badge if any feature needs the user.

- **Open** = effective status not in {`Released`, `Cancelled`}.
- **Shipped** ones go in a collapsed "Shipped" section (REQ-009).
- Hotfix folders are visually distinct (red accent, 🔥) and listed with releases, sorted by "needs attention" first, then most recent activity.
- Actions: **＋ New release**, **＋ Urgent fix** (the existing create flows, moved here — reuse their handlers).

```gherkin
Feature: Project home

  Scenario: A project with one release and one hotfix
    Given a release "adding" and a hotfix "div-by-zero" that are both open
    When the user opens the project
    Then both appear as folders
    And the hotfix folder is visibly different from the release folder

  Scenario: A project with nothing yet
    Given no releases or hotfixes
    When the user opens the project
    Then they see a friendly "Start your first release" call to action
    And no empty list is shown

  Scenario: A folder needs the user
    Given a release containing a feature waiting for approval
    When the user opens the project
    Then that folder shows a "Needs you" badge

  Scenario: Opening a folder
    Given the project home is showing
    When the user clicks the release folder
    Then the address becomes "#/r/<releaseId>"
    And the release's features are shown
```

---

### REQ-003 — Release folder shows features  · MUST · Phase 2

Lists **every** feature in the release — done, in progress, paused, not started — as cards.

Card content: name, plain status, **stage progress** (current stage + "stage 2 of 3"), last activity, attention badge, and (if paused) a "Paused" tag. Ordered: needs-you first, in-progress, paused, not started, done.

Always visible at the top: **＋ New feature** (a "new folder"-style tile — never hidden in a collapsible). When every feature is Done, a **Ship this release** button appears (reuse `ShipReleaseButton`).

```gherkin
Feature: Release folder

  Scenario: A release with a finished feature and a new one
    Given a release with "adding" done and "subtraction" in progress
    When the user opens the release
    Then both features appear as cards
    And "subtraction" shows its current stage
    And "adding" shows "Done"

  Scenario: Adding a feature is always possible
    Given a release whose features are all done
    When the user opens the release
    Then "＋ New feature" is visible without scrolling or expanding anything

  Scenario: Shipping is offered only when everything is done
    Given a release with one done and one in-progress feature
    Then "Ship this release" is not shown
```

---

### REQ-004 — Creating a feature is honest about its consequence  · MUST · Phase 2

The new-feature control asks for a name only. If **another feature is currently active in this project**, show before confirming:

> "**"subtraction"** will become your active feature. **"multiplication"** will be paused and can be resumed later."

On success: navigate to the new feature's view. The release's effective status must read "In progress" (REQ-001), so its workflow renders.

```gherkin
Feature: New feature

  Scenario: Another feature is active
    Given "multiplication" is the active feature
    When the user starts a new feature "division"
    Then they are told "multiplication" will be paused
    And after confirming they land on "division"'s workflow

  Scenario: Nothing is active
    Given no feature is active
    When the user starts a new feature
    Then no pause warning is shown

  Scenario: The name is empty or already used
    When the user submits a blank name or one that already exists in the release
    Then a plain-language error is shown
    And nothing is created
```

---

### REQ-005 — Feature view opens the right workflow, for any feature  · MUST · Phase 1–2

Opening a feature shows its pipeline stepper and the stage screen **for that feature**, using the adapter (§6.2).

| Feature state | What opens |
|---|---|
| In progress **and active** | The live workflow at its **current stage** (chat / log / checks, all actions enabled). |
| In progress but **not active** (Paused) | The same workflow **read-only** + banner (REQ-006). |
| **Done** | Read-only: every stage's history, logs, checks, findings, chat transcript, artifacts. |
| Not started | The workflow at its first stage; starting it makes it active (REQ-006 action). |

```gherkin
Feature: Feature view

  Scenario: An in-progress feature
    Given "subtraction" is active at the developer stage
    When the user opens it
    Then the developer stage is shown with its normal controls

  Scenario: A finished feature
    Given "adding" is Done
    When the user opens it
    Then they can browse each stage's history and logs
    And no button that runs the agent or changes state is shown

  Scenario: Reload keeps your place
    Given the user is on "#/r/<id>/f/<id>"
    When they refresh the browser
    Then the same feature view is shown

  Scenario: The feature no longer exists
    Given the address names a feature that isn't in the release
    Then a friendly "We couldn't find that feature" message is shown with a link back to the release
```

---

### REQ-006 — Looking never changes anything; resuming is explicit  · MUST · Phase 2

- Navigating (any level) **must not** call `switchFeatureAsync`, `startStageAsync`, or any state-changing endpoint. ⚠️ Assert this in tests.
- A **Paused** feature shows a banner: *"This feature is paused. Resume work on it to continue."* with a **Resume work here** button (calls the existing `switchFeatureAsync`).
- Until resumed, everything that would run the agent or change state is disabled/hidden: send message, run stage, run checks, approve, push back, model switch, retry. (Implemented via a `readOnly` flag; §6.3.)
- If the resume switches away from another feature, that feature becomes Paused (server already does this).

```gherkin
Feature: Read-only until resumed

  Scenario: Opening a paused feature
    Given "multiplication" is paused
    When the user opens it
    Then the stage is shown
    And a banner says it is paused
    And the chat input, run and approve buttons are not available

  Scenario: Navigating never touches git
    When the user opens releases and features
    Then no request that changes state is sent

  Scenario: Resuming
    Given a paused feature is open
    When the user clicks "Resume work here"
    Then the switch request is sent
    And when it succeeds the feature's normal controls appear
```

---

### REQ-007 — Earlier stages can be peeked at  · SHOULD · Phase 2

Each stage in the stepper is clickable. Clicking a **past** stage opens a **read-only** view of that stage's latest run (address `…/s/<stageName>`); a "Back to current stage" link returns. Clicking a **future** stage shows "Not reached yet".

```gherkin
Feature: Peeking at earlier stages

  Scenario: Reading the requirements while developing
    Given the feature is at the developer stage
    When the user clicks the business-analyst stage
    Then that stage's history and files are shown read-only
    And a "Back to current stage" link is available
```

---

### REQ-008 — Attention badges  · SHOULD · Phase 3

A feature **needs the user** when its latest run is `BlockedSignoff`, `BlockedGate`, `BlockedEntry`, or `Escalated`, or `readyToProceed` with an unapproved signoff. A folder needs the user if any feature does. Badge text is plain ("Needs your approval", "Some checks didn't pass", "The AI service needs attention") — see §3.1.

Derived by a **pure function** `attentionFor(feature)` returning `{ level: "none" | "info" | "needs-you", reason?: string }` (unit-tested; no React).

```gherkin
Feature: Attention

  Scenario Outline: Reasons a feature needs the user
    Given a feature whose latest stage run is "<status>"
    Then its badge reads "<text>"
    Examples:
      | status         | text                            |
      | BlockedSignoff | Needs your approval             |
      | BlockedGate    | Some checks didn't pass         |
      | Escalated      | The AI service needs attention  |

  Scenario: Nothing to do
    Given a feature whose latest run is Active with no problems
    Then no badge is shown
```

---

### REQ-009 — Shipped releases have a home  · COULD · Phase 3

A collapsed **Shipped (n)** section at the bottom of the project home lists releases whose effective status is `Released`. Opening one uses the same folder view, fully read-only.

### REQ-010 — Breadcrumbs, back, deep links  · MUST · Phase 2

- A breadcrumb on every level (§4). Every crumb is a real link (`<a href="#/…">`).
- The browser back/forward buttons work.
- Unknown routes fall back to project home with no error.
- Route parsing/building lives in **one pure module** (`routes.ts`) with round-trip tests.

```gherkin
Feature: Routing

  Scenario Outline: Route round-trips
    Given the route "<route>"
    When it is parsed and then built again
    Then the result equals "<route>"
    Examples:
      | route                          |
      | #/                             |
      | #/r/abc                        |
      | #/r/abc/f/def                  |
      | #/r/abc/f/def/s/developer      |

  Scenario: Garbage in the address
    Given the address is "#/r/../../x"
    Then the project home is shown

  Scenario: Back button
    Given the user went home → release → feature
    When they press Back
    Then the release folder is shown
```

---

### REQ-011 — Every screen has good loading, empty and error states  · MUST

| State | Requirement |
|---|---|
| Loading | Skeleton or "Loading…" — never a blank area. |
| Empty | A helpful sentence and the next action (e.g. "＋ New feature"). |
| Error | Plain sentence + **Try again**; never a raw stack trace or `Broker POST /api/… failed with 500:`. Use `toErrorMessage`. |
| Backend down | One clear banner ("We can't reach DevTeam right now. Retrying…") — polling already retries with backoff. |

### REQ-012 — Accessibility and small screens  · SHOULD

Folders/cards are real links or buttons (keyboard-focusable, Enter/Space opens), the breadcrumb has `aria-label="Breadcrumb"` and the last crumb `aria-current="page"`, attention badges have text (not colour alone), and the layout works at phone width (cards stack; no horizontal scroll).

### REQ-013 — Preserve everything that already works  · MUST

- The existing stage screen, chat, log, diagnostics, push-back, model-problem pane, gate-repair notice and all their `data-testid`s keep working **unchanged**.
- The existing `ReleaseWizard.test.tsx` suite stays green (it may be *extended*; if a test must change because the old three-view flow is replaced, the PR says which and why).
- Backend: no behavioural change beyond REQ-001's additive property.

## 6. Design

### 6.1 New files (all additive)

```
front-end/app/Project/Navigation/
  routes.ts                pure: parseRoute(hash) / buildRoute(route) / crumbs(route, data)
  useHashRoute.ts          hook: current route + navigate(); listens to 'hashchange'
  terms.ts                 the §3.1 nouns
  releaseView.ts           pure: releaseForFeature, effectiveReleaseStatus, attentionFor,
                                 featureProgress, folderSummary, sortFeatures
  ProjectHome.tsx          Level 0 (folders, shipped section, empty state)
  ReleaseFolder.tsx        Level 1 (feature cards, new-feature tile, ship button)
  FeatureView.tsx          Level 2 (banner + stepper + re-pointed stage screen)
  NewFeatureTile.tsx       REQ-004
  Breadcrumb.tsx
  *.test.ts(x)             one per file
front-end/app/Project/Styles/Navigation.module.css
```

`ReleaseWizard.tsx` is **not** rewritten. Step 1 of Phase 2 is a **pure move**: extract `StageScreen`, `ChatStage`, `StageLog`, `ModelProblemPane`, etc. into `Project/Release/Stage/*.tsx` and re-export, with tests green before and after. Then the new shell mounts them.

### 6.2 The adapter (the key trick)

```ts
// releaseView.ts
export function releaseForFeature(release: ReleaseDto, featureId: string): ReleaseDto {
  const f = release.features.find((x) => x.id === featureId);
  if (!f) return release;
  return {
    ...release,
    currentFeatureId: f.id,
    stageRuns: f.stageRuns ?? [],
    signoffs: f.signoffs ?? [],
    flowPosition: f.flowPosition ?? null,
    // A feature view is never "release complete": that banner belongs to the folder.
    status: effectiveReleaseStatus(release) === "Ready" ? "InProgress" : effectiveReleaseStatus(release),
  };
}
```

Feed that to the existing components and they show **that feature**. It is pure, so it is trivially unit-tested (§11). ⚠️ It must never mutate its input.

### 6.3 Read-only mode

Add an **optional** prop `readOnly?: boolean` (default `false`) to `StageScreen`, `ChatStage`, `StageLog`, `StageCompleteCard` and the approve/push-back controls. When true: hide/disable every control that runs the agent or changes state; keep all viewing (messages, logs, artifacts, diagnostics). Default `false` means **existing behaviour and tests are unchanged**. ⚠️ Don't invert this (default true) — it would silently disable production screens.

### 6.4 Data flow and polling

- Level 0/1 use **one** `listReleasesAsync` + `listHotfixesAsync` (already returns features with runs). Poll gently (10 s, back off) — reuse `usePoller`.
- Level 2 polls `getReleaseAsync(releaseId)` (3 s, as `ReleaseDetail` does today) **only while a feature view is mounted**. ⚠️ Never poll at more than one level at once (pitfall 7).
- Pipeline stages are the same for every feature in a project: fetch `getPipelineAsync(anyFeatureId)` **once per project** and cache it for progress counts ("stage 2 of 3").
- "Active feature" for a project = the release payload's `currentFeatureId` (derived server-side from the workspace checkout). ⚠️ It is set on **whichever release owns the active feature**; other releases report `null`. To know "is anything active in this project?" scan all releases.

### 6.5 Backend (Phase 0 only)

```csharp
// Workflow/ReleaseStatusRules.cs — new, pure
public static class ReleaseStatusRules
{
    public static ReleaseStatus Effective(ReleaseStatus stored, IReadOnlyCollection<ReleaseFeatureStatus> features)
        => stored == ReleaseStatus.Ready
            && (features.Count == 0 || features.Any(s => s != ReleaseFeatureStatus.Complete))
            ? ReleaseStatus.InProgress
            : stored;
}

// Domain/ReleaseEntities.cs — DevTeamRelease: ADD one property (do not edit existing members)
public ReleaseStatus EffectiveStatus => ReleaseStatusRules.Effective(Status, Features.Select(f => f.Status).ToList());
```

Check the JSON serializer setup already writes enums as strings (an existing test `GetRelease_SerializesEnumsAsStringsNotIntegers` proves it) so `effectiveStatus` arrives as `"InProgress"`.

Add `effectiveStatus?: string` to `ReleaseDto` in `BrokerTypes.ts`.

## 7. Files you will touch (and how)

> **Team rule: composition over modification.** Add new files/methods. Don't edit the body of a working method. Exceptions are listed here with the smallest possible edit.

| File | Change | Kind |
|---|---|---|
| `back-end/.../Workflow/ReleaseStatusRules.cs` | new | additive |
| `back-end/.../Domain/ReleaseEntities.cs` | one new get-only property on `DevTeamRelease` | additive |
| `back-end/DevTeam.Tests/ReleaseStatusRulesTests.cs` | new | additive |
| `front-end/app/Chat/Data/BrokerTypes.ts` | `effectiveStatus?: string` on `ReleaseDto` | additive |
| `front-end/app/Project/Release/ReleaseWizard.tsx` | ⚠️ `StageScreen` early-return uses `effectiveReleaseStatus(release)`; `ShipReleaseButton` gate; list/status chips; add `readOnly?` prop plumbing; later: extract components | small edits, each test-covered |
| `front-end/app/App.tsx` | mount the new shell instead of `ReleaseWizard` (Phase 2) | one line |
| `front-end/app/Project/Navigation/**` | new | additive |
| `front-end/app/Project/Styles/Navigation.module.css` | new | additive |

## 8. Configuration

None. (Optional later: a "Show shipped" persisted preference in `localStorage` — wrap every access in try/catch, it can throw in private mode.)

## 9. Worked examples

### 9.1 Your scenario, end to end (after the redesign)

1. Open `D:\apps\calculator` → **Project home**: folder **📁 Release "adding"** — "1 of 1 features done · Ready to ship".
2. Click it → **Release folder**: card **adding** (Done). Big tile **＋ New feature**.
3. Click **＋ New feature**, type `subtraction`. No other feature is active, so no pause warning. Confirm.
4. Server creates the feature and checks it out. The release's effective status becomes "In progress" (REQ-001), so the folder card now says "1 of 2 features done".
5. You land on **#/r/…/f/…**: breadcrumb `Calculator › Release "adding" › subtraction`; the stepper shows business-analyst as the current stage; chat is live.
6. Click **adding** in the breadcrumb's folder → click card **adding** (Done) → read-only: click *developer* in the stepper to see its log and checks. Nothing on this page can change anything, and no git command ran.

### 9.2 A paused feature

`multiplication` is paused because you started `subtraction`. Open it: banner "This feature is paused"; chat input hidden; stepper and history browsable. Click **Resume work here** → `subtraction` becomes paused, `multiplication` active, controls appear.

### 9.3 Sample data → card

```json
{ "key": "subtraction", "status": "InProgress",
  "flowPosition": { "currentStageName": "developer", "currentStageIndex": 1 },
  "stageRuns": [ { "stageName": "developer", "status": "BlockedSignoff", "readyToProceed": true } ] }
```
→ card: **subtraction · Working — Developer (stage 2 of 3) · 🔔 Needs your approval**.

## 10. Build order (each step ends green and shippable)

| # | Step | Ships value? |
|---|---|---|
| 0a | Backend `ReleaseStatusRules` + `EffectiveStatus` + tests | ✅ (data correct) |
| 0b | Frontend: `effectiveReleaseStatus` used in `StageScreen`/Ship gate/status chips; regression test for "adding done → subtraction in progress" | ✅ **fixes the reported bug** |
| 1 | `releaseView.ts` pure helpers (`releaseForFeature`, `attentionFor`, `featureProgress`, `folderSummary`, `sortFeatures`) + `terms.ts` + `routes.ts` + tests | plumbing |
| 2 | `readOnly` plumbing through the stage components (default false) + tests | plumbing |
| 3 | Pure move: extract stage components out of `ReleaseWizard.tsx` (tests green before/after) | refactor |
| 4 | `useHashRoute` + `Breadcrumb` | plumbing |
| 5 | `FeatureView` (adapter + banner + peek) | ✅ view any feature |
| 6 | `ReleaseFolder` + `NewFeatureTile` | ✅ |
| 7 | `ProjectHome` + mount in `App.tsx` (old `ReleaseWizard` list/detail retired) | ✅ the redesign |
| 8 | Attention badges, shipped section, empty/error polish, a11y pass | ✅ |

Ship 0a–0b immediately. Step 7 last among the big ones, because it swaps what users see.

## 11. Test plan (write FIRST — project rule)

Backend: xUnit (`DevTeam.Tests`), style reference `GateSelfHealerTests.cs`. Frontend: Jest + React Testing Library, style reference `ReleaseWizard.test.tsx` (has `makeRun`, `makeRelease`, `openDetail`, and a `pickReactSelectOption` pattern for `SelectWrapper`).

| Area | Tests |
|---|---|
| `ReleaseStatusRules` | every row of the §5 REQ-001 table; empty feature list; doesn't touch stored value; JSON contains `effectiveStatus` as a string |
| `effectiveReleaseStatus` (TS) | prefers `effectiveStatus`; falls back to `status`; falls back to computing from features |
| `releaseForFeature` | re-points proxies; **doesn't mutate input**; unknown id returns input; never yields "Ready" for a feature view |
| `attentionFor` / `featureProgress` / `folderSummary` / `sortFeatures` | table-driven, all statuses, empty runs, missing `flowPosition` |
| `routes` | round-trips; garbage → home; path traversal-looking input; encoded ids |
| `StageScreen` (regression) | release stored "Ready" **with a new in-progress feature** renders the workflow, not "Release complete" |
| `readOnly` | each control hidden/disabled when true; **unchanged when omitted** |
| `FeatureView` | in-progress active / paused / done / not-found; **no state-changing API called on open** (`expect(api.switchFeatureAsync).not.toHaveBeenCalled()` etc.); resume calls switch once |
| `ReleaseFolder` | cards for all statuses; ＋ New feature always visible; ship button only when all done |
| `NewFeatureTile` | pause warning only when another feature is active; blank/duplicate name errors |
| `ProjectHome` | folders, hotfix styling, empty state, attention badge, shipped section collapsed |
| Routing integration | back/forward; refresh keeps place; hash only written while the Releases tab is active |
| Wording | one test scans all user-visible strings in the new components for banned words: `checkout, stash, branch, HEAD, gate, gherkin, escalated, BlockedSignoff` |

Definition of "tests done": written first, failed for the right reason, then passed; **entire** suites pass.

⚠️ **Known red tests:** four tests in `WorkflowResilienceTests` fail independent of this work. Compare failing lists before/after; don't "fix" them here and don't let them hide a new failure. The frontend suite has one timing-sensitive test (`does not force-scroll to bottom…`) that can flake under full-suite load and passes alone.

## 12. Pitfalls (read before you code)

1. **Don't "fix" the bug in `FinalizeFeatureCompletionAsync`/`CreateFeatureAsync`.** It looks like a one-line reset, but `Ready` is also set when *other* features are still open, and a stored value can go stale in other ways. Derive it (REQ-001). It also honours the no-edit rule.
2. **`release.stageRuns/signoffs/flowPosition` are proxies of the current feature only.** Anything that reads them for a non-current feature is silently wrong. Always go through `releaseForFeature`.
3. **Looking must not have side effects.** `switchFeatureAsync` stashes work and changes branches. Never call it from navigation, `useEffect`, hover, or prefetch. Test for it.
4. **Two releases can share one project.** `currentFeatureId` is derived from the *workspace's* active checkout and is only populated on the release that owns that feature. Release B can show "no active feature" while a feature in Release A is active. Compute "active anywhere" across all releases.
5. **Hotfixes are separate lists.** `listReleasesAsync` excludes hotfixes (`.Where(r => !r.IsHotfix)`); use `listHotfixesAsync`. A hotfix's release-shell has one feature.
6. **The Releases tab stays mounted while hidden** (`<div hidden>` in `App.tsx`). Only write `location.hash` while it's the active tab, and re-read the hash when the tab becomes active again — otherwise Git/Settings visits corrupt or reset your route.
7. **Polling multiplies.** Old `ReleaseDetail` polls every 3 s; each stage panel also polls messages/turns. Mounting Level 1 *and* Level 2 polling simultaneously doubles load. Poll only at the level on screen; unmount stops the poller.
8. **`ZestButton` has a ~2 s post-click cool-down** (`minBusyDurationMs` 500, `autoResetAfterMs` 2000) that greys the button and shows a tick. In "try again" flows set `busyOptions: { minBusyDurationMs: 0 }` and a short `successOptions.autoResetAfterMs` — see `ModelProblemPane`. In tests, use `waitFor` for re-enabled state.
9. **`SelectWrapper` is react-select**, not a native `<select>`. Test it with `.react-select__control` mouseDown then click the option text (helper in `PipelineView.test.tsx`). Options must be objects, not strings.
10. **`jest.mock("…/BrokerApi")` automocks the class.** A new API method must exist on the prototype or the mock lacks it. Add methods to `BrokerApi.ts` before writing tests that call them.
11. **Hash routing + `hashchange`.** `location.hash = x` fires `hashchange` asynchronously; don't assume state has updated on the next line. In tests dispatch `new HashChangeEvent("hashchange")` after setting the hash, and reset `location.hash = ""` in `afterEach`.
12. **IDs in URLs.** Ids are GUIDs; `encodeURIComponent` them anyway and validate on parse (`/^[0-9a-f-]{36}$/i`). An id in the address that doesn't exist must render the "not found" state, never throw.
13. **Stale selection.** The release you're viewing can vanish or change (another window ships it). Derive the view from the polled data every render; never keep a copy of the release in `useState` as the source of truth (the old code does: `selectedRelease`).
14. **Date/time.** "Last activity" = max of feature `updatedAt` and its runs' `finishedAt/startedAt`, displayed relative ("2 hours ago"). Inject a clock in tests; don't let snapshots depend on the current time.
15. **Sorting must be stable and deterministic** (tie-break by `createdAt`, then `key`) or cards jump on every poll.
16. **Don't hide state behind colour.** Attention must have text and an icon, not just a colour.
17. **Line endings.** The repo mixes CRLF/LF (git warns on some files). When you move code between files, don't reformat; keep the diff reviewable. Do the extraction as its own commit with no other changes.
18. **The old `ReleaseWizard.test.tsx` uses `data-testid`s like `release-item-<id>`, `release-move-on-btn`.** If the folder cards replace the old list, keep equivalent ids or update those tests deliberately and say so in the PR.
19. **Don't reuse "release complete" wording for a feature.** A finished *feature* is "Done"; only a release whose features are *all* done is "Ready to ship"; only a *shipped* release is "Shipped".
20. **Local-only browser storage can throw** (private mode, blocked cookies). Any `localStorage` use is wrapped in try/catch and the UI works without it.

## 13. Best practices & Definition of Done

**Best practices**

- Put logic in **pure functions** (`releaseView.ts`, `routes.ts`) and keep components thin. Pure functions are cheap to test exhaustively.
- Derive, don't store: compute view state from the polled release each render.
- One source of truth for words (`terms.ts` + `labels.ts`) and a test that scans for banned words.
- Small components, one job each, props typed with the existing DTOs; no `any`.
- Reuse `usePoller`, `toErrorMessage`, `statusColor`, `StageScreen`, `ShipReleaseButton`, `CreateFeatureForm` logic — don't reimplement.
- Comments explain *why* (match existing style).

**Definition of done** — all true:

- [ ] Tests written first; new tests fail before the code and pass after.
- [ ] Backend `dotnet test` and frontend `npx jest` are green apart from the known items in §11; `npx tsc --noEmit` is clean.
- [ ] The reported scenario works: release with a done feature → add `subtraction` → its workflow appears (manually verified once, against a scratch project).
- [ ] Opening any feature (including done and paused) makes **zero** state-changing requests (asserted in a test).
- [ ] Refresh and back/forward keep your place.
- [ ] No git vocabulary or internal status names anywhere the user can read (wording test passes).
- [ ] Existing stage-screen behaviour and test ids unchanged.
- [ ] Empty, loading and error states present at each level.
- [ ] Keyboard-only walk-through works (Tab to a folder, Enter opens, breadcrumb links work).
- [ ] PR description lists: decisions taken from §14, existing tests changed and why, anything deferred.

## 14. Open decisions (safe defaults in bold — build proceeds with them)

1. **Wording:** "Hotfix" → **"Urgent fix"**; "Release" stays **"Release"**. Change in `terms.ts` only.
2. **Hotfix folders:** **open straight into the single feature**, breadcrumb still shows the folder.
3. **Shipped section:** **collapsed by default**, not persisted.
4. **Feature ordering:** **needs-you → in progress → paused → not started → done.**
5. **Route style:** **hash routes** (§4.1).
6. **Old UI:** keep `ReleaseWizard` list/detail code until Phase 2 step 7 lands and is verified, then remove it in a separate commit.
7. **Progress denominator:** **number of stages in the project's pipeline** (fetched once), not a percentage.

## 15. FAQ

**Why not just fix the status in the backend where it is set?** See pitfall 1: it is set for the wrong reason in more than one place, and stored derived values go stale. Deriving is safer and additive.

**Why no new endpoint?** The release payload already carries every feature's runs, gate checks, findings and signoffs (§1.4). Adding an endpoint would duplicate it.

**Will the old tests break?** They exercise the stage screen, which is untouched. Tests of the three-view list/detail flow may need updating when that flow is replaced (Phase 2 step 7) — do that in the same commit and note it.

**What if a feature is paused and its agent is mid-turn?** It isn't: pausing happens when another feature is checked out; the server already parks it. The paused view is static data plus transcripts.

**Do we need real-time (SignalR) updates?** Not for this work — polling exists. Attention badges refresh on the poll.

## 16. Glossary of paths

| Path | Purpose |
|---|---|
| `#/` | Project home |
| `#/r/<releaseId>` | Release (or urgent-fix) folder |
| `#/r/<releaseId>/f/<featureId>` | Feature workflow (live or read-only) |
| `#/r/<releaseId>/f/<featureId>/s/<stageName>` | Read-only peek at one stage |
| `front-end/app/Project/Navigation/` | New navigation code |
| `front-end/app/Project/Release/Stage/` | Extracted stage components (after the pure move) |
| `back-end/DevTeam.Broker/Workflow/ReleaseStatusRules.cs` | Derived release status |
