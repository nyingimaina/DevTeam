# last-feature — Business Requirements Specification

| | |
|---|---|
| **Feature** | `last-feature` |
| **Status** | Draft — complete; ready for signoff |
| **Audience** | Any developer who builds this. You should not need to ask anyone anything. |
| **Scope** | Starting a release (or hotfix) must no longer auto-create a feature named after the release. The release is created empty; the user lands on an empty-state screen that explains there are no features yet and offers a single CTA to create the first feature, whose name the user chooses. |
| **Related** | `interview.md` (intake Q1–Q9), `handoff.md`, `manifest.yaml` |

---

## 0. How to use this document

1. Every requirement has a **REQ-N** id and one Given/When/Then acceptance criterion.
2. Test commands: backend `dotnet test DevTeam.slnx`; frontend `npm test` from `front-end/`.

---

## 1. Executive summary & scope

### 1.1 Business objective

Today, when a user starts a release, DevTeam automatically creates a feature that reuses the release's own name. That is wrong: a release is a container that may hold many features, and the first feature should not be conjured from the release's name. This feature changes the flow so a release (and a hotfix) is created with **zero features and zero branches**, and the user lands on an empty-state screen that explains the situation in plain language and offers one clear action — **Add feature** — to create the first feature under a name the user chooses.

### 1.2 System context & boundaries

- **In scope:** release and hotfix creation (backend `WorkflowEngine.StartReleaseAsync` / `StartHotfixAsync`), the release screen's empty state and its create-first-feature CTA (frontend navigation shell), and the end-to-end rename of the create input/parameter from a feature key to a release key.
- **Out of scope (do not build):** changing the stage pipeline, gates, sign-off, or merge behaviour for features that do exist; changing how features behave after they are created; any change to the legacy `ReleaseWizard` screen.
- **Existing behaviour preserved:** a feature, once created, still owns its `feature/<key>` branch and merges into the release branch exactly as before; two releases can still share a workspace; the previously active feature is untouched by starting a featureless release.

## 2. Agreed decisions (recorded)

| # | Decision | Value |
|---|---|---|
| D1 | No auto-created feature | Starting a release creates the release shell only; no feature is created for the user. |
| D2 | Release input relabel | The single create input is a **Release Key**; features get their own keys later. |
| D3 | Scope of the fix | Applies to **both** releases and hotfixes — same empty state and CTA. |
| D4 | CTA behaviour | Clicking **Add feature** expands an inline form, reusing the existing create-feature form. |
| D5 | Branches | **No branches at all** are created until the first feature is added. |
| D6 | Empty-state copy | Heading **"No features yet"** + **"This release doesn't have any features yet. Add one to begin the workflow."** + button **"Add feature"**. |
| D7 | Active checkout | The previously active feature is left untouched — a featureless release never disturbs a checkout. |
| D8 | Parameter rename | Renamed end-to-end: endpoint/DTO field and client parameter become `ReleaseKey` / `releaseKey`. |
| D9 | Verification | A regression test asserts the posted create-release body uses `releaseKey`. |

## 3. Requirements list

Every requirement below is a `## REQ-N: <Title>` section with a Given/When/Then example.

| ID | Title | Priority |
|---|---|---|
| REQ-1 | Starting a release creates no feature | MUST |
| REQ-2 | The create input is a release key, sent as `releaseKey` | MUST |
| REQ-3 | No branches until the first feature is added | MUST |
| REQ-4 | The user lands on the release's empty state | MUST |
| REQ-5 | The empty state explains and offers the "Add feature" CTA | MUST |
| REQ-6 | The CTA expands an inline form for the first feature | MUST |
| REQ-7 | The first feature owns the branches and becomes active | MUST |
| REQ-8 | Hotfixes behave exactly the same | MUST |
| REQ-9 | A featureless release leaves the active checkout untouched | MUST |
| REQ-10 | "Add feature" is the CTA label on every release | MUST |
| REQ-11 | Plain language, no git or internal vocabulary (SYS-NFR-UX-1) | MUST |
| REQ-12 | Existing test suites stay green | MUST |

## 4. Functional requirements

## REQ-1: Starting a release creates no feature

Starting a release SHALL create the release shell only; it SHALL NOT auto-create a feature, and no feature SHALL reuse the release's name.

```gherkin
Given a user with no release named "negation"
When they start a release with the key "negation"
Then the release "negation" exists
And the release has zero features
And no feature named "negation" was created
```

## REQ-2: The create input is a release key, sent as `releaseKey`

The single input on the create-release form SHALL be the release key, and its value SHALL be sent in the request body as `releaseKey`.

```gherkin
Given a user opens the create-release form
When they submit the key "negation"
Then the posted request body contains releaseKey = "negation"
And the created release is titled from "negation", not from a feature key
```

## REQ-3: No branches until the first feature is added

Starting a release or hotfix SHALL create no git branches, SHALL NOT check out anything, and SHALL NOT create stage sign-offs — all of that happens when the first feature is added.

```gherkin
Given a fresh workspace
When a release "negation" is started
Then no release/negation branch exists
And no feature/ branch exists
And the workspace checkout is unchanged
```

## REQ-4: The user lands on the release's empty state

Immediately after a release is created, the user SHALL be taken to that release's screen, which SHALL present the empty state.

```gherkin
Given a user starts release "negation"
When it is created
Then they land on the "negation" release screen
And the screen shows the heading "No features yet"
```

## REQ-5: The empty state explains and offers the "Add feature" CTA

A release with zero features SHALL show, in order: the heading **"No features yet"**, the body **"This release doesn't have any features yet. Add one to begin the workflow."**, and a primary button labelled **"Add feature"**.

```gherkin
Given a release with zero features
When its screen is displayed
Then the heading "No features yet" is shown
And the text "This release doesn't have any features yet. Add one to begin the workflow." is shown
And a button labelled "Add feature" is shown
```

## REQ-6: The CTA expands an inline form for the first feature

Clicking **Add feature** SHALL reveal an inline form (reusing the existing create-feature form) with a feature-name field, and the user SHALL be able to name the feature however they please.

```gherkin
Given a release on its empty state
When the user clicks "Add feature"
Then an inline form with a feature-name field is shown
And the user can type any name they choose
And submitting creates exactly one feature with that name
```

## REQ-7: The first feature owns the branches and becomes active

Creating the first feature SHALL create the release branch from the release key and the feature's own branch, and SHALL make that feature the active checkout.

```gherkin
Given release "negation" with zero features
When the user adds feature "password-reset"
Then branch release/negation is created
And branch feature/password-reset is created and checked out
And "password-reset" is the release's active feature
```

## REQ-8: Hotfixes behave exactly the same

Starting a hotfix SHALL also create an empty shell that lands the user on the same empty state with the **Add feature** CTA.

```gherkin
Given a user starts an urgent fix "critical-bug"
When it is created
Then the hotfix has zero features
And the user lands on its empty state
And a button labelled "Add feature" is shown
```

## REQ-9: A featureless release leaves the active checkout untouched

Starting a release or hotfix with no features SHALL NOT put another feature on hold and SHALL NOT change which feature is checked out.

```gherkin
Given feature "subtraction" is the active checkout in the workspace
When a new release is started with no features
Then "subtraction" is still the active checkout
And its status is unchanged
```

## REQ-10: "Add feature" is the CTA label on every release

The always-visible add-feature control on a release SHALL be labelled **"Add feature"**, both on the empty state and when the release already has features.

```gherkin
Given a release that already has at least one feature
When its screen is displayed
Then the add-feature control is labelled "Add feature"
```

## 5. Non-functional requirements

## REQ-11: Plain language, no git or internal vocabulary

The empty state, its CTA, and its inline form SHALL use plain words and SHALL NOT show internal terms such as branch, checkout, stash, or HEAD.

```gherkin
Given a release with zero features
When the empty state is displayed
Then no internal term such as "branch", "checkout", "stash", or "HEAD" is shown
```

## REQ-12: Existing test suites stay green

The change SHALL keep the backend (`dotnet test DevTeam.slnx`) and frontend (`npm test`) suites passing, including a regression test that asserts the create-release request body uses `releaseKey`.

```gherkin
Given the change is merged
When the backend and frontend test suites run
Then both suites pass
And a regression test confirms the posted create-release body uses releaseKey
```

## 6. Traceability & verification

| ID | Priority | Verification method |
|---|---|---|
| REQ-1 | MUST | `WorkflowEngineTests` — start a release, assert zero features and no same-named feature |
| REQ-2 | MUST | `BrokerApi.test.ts` — assert the posted body carries `releaseKey` |
| REQ-3 | MUST | `WorkflowEngineTests` — assert no branches/checkout created at release creation |
| REQ-4 | MUST | `ProjectHome.test.tsx` — assert navigation into the new release |
| REQ-5 | MUST | `ReleaseFolder.test.tsx` — assert heading, body, and "Add feature" button |
| REQ-6 | MUST | `ReleaseFolder.test.tsx` — click the CTA, assert the inline form and creation |
| REQ-7 | MUST | `WorkflowEngineTests` — first feature creates branches and becomes active |
| REQ-8 | MUST | Hotfix tests — empty hotfix shell lands on the empty state |
| REQ-9 | MUST | `WorkflowEngineTests` — active feature unchanged after a featureless release |
| REQ-10 | MUST | `ReleaseFolder.test.tsx` — non-empty release shows the "Add feature" label |
| REQ-11 | MUST | `ReleaseFolder.test.tsx` — assert no internal vocabulary in the rendered screen |
| REQ-12 | MUST | Run `dotnet test DevTeam.slnx` and `npm test` |

## 7. Open decisions

None — all intake questions are resolved (see §2).
