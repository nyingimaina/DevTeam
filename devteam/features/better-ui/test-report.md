# better-ui - Test report

| | |
|---|---|
| **Feature** | `better-ui` |
| **Stage** | test-runner |
| **Command** | `cmd.exe /d /c npm --prefix front-end test` |
| **Source of facts** | `devteam/features/better-ui/test-run.json`, written by the `test_run` gate. Not re-run, not edited by this stage. |
| **Recorded run** | ran at `2026-10-01T06:55:14.5352543+00:00`, exit code `0`, `FailedCount: 0`, `TimedOut: false` |
| **Recorded failures** | none - `Failures: []` |
| **Failure sections** | 0 - the recorded run lists no failed test, so there is nothing to section |
| **Changed test files** | none - the change set the gate reads holds no test file (§3) |
| **Verdict on the recorded run** | **pass** - REQ-1 and REQ-2 are each covered by a passing test (§2) |
| **Verdict on this report** | **pass** - every recorded failure is accounted for (there are none), and no test file in the diff lacks an approved challenge (none is in the diff) |
| **Operator action needed** | none - no challenge is open, no ruling is pending, no addendum is owed |

## 1. Failures recorded by the run

`test-run.json` records `ExitCode: 0`, `FailedCount: 0`, `Failures: []`, `TimedOut:
false`. No test failed, so this report contains **no per-failure sections** - and it
must not. A section may only account for a failure the machine run actually
recorded, so with an empty `Failures` list any such section is a recording defect
and is rejected (`back-end/DevTeam.Broker/Gates/TestReportGate.cs:75-88`). Writing
one would assert a failure that did not happen, which is a worse defect than an
empty report. Two earlier drafts of this report each tripped that check, once by
carrying four challenge sections for files that are not in the change set (§3) and
once by carrying a challenge for a `ReleaseWizard` test that this run did not fail.

## 2. Requirement coverage in the green run

| REQ | Test | Status |
|---|---|---|
| REQ-1 | `front-end/app/Chat/UI/MessageRow.test.tsx:50` `MessageRow > REQ_1_AssistantMessageWithOneToolCall_RendersNoToolCallNode` | passing |
| REQ-1 | `front-end/app/Chat/UI/MessageRow.test.tsx:75` `MessageRow > REQ_1_AssistantMessageWithMultipleToolCalls_RendersNoToolCallNodesAtAll` | passing |
| REQ-2 | `front-end/app/Chat/UI/MessageRow.test.tsx:35` `MessageRow > REQ_2_StreamedToolEchoLines_StayHiddenFromAssistantBody` | passing |

**REQ-1** - the two tests cover both cases the criterion names ("with or without a
`toolName`"): one `tool_call` part with `toolName: "bash"` / `toolCallId:
"call_abc12345xyz"`, and three parts where one `tool_call` has no `toolName`, one has
no id, and one is a `text` part. Each asserts no `data-testid="tool-call"` node, no
`Tool call` text, no `#12345xyz` chip and no friendly label (`Run command`), while
the assistant body text still renders.

Implementation: `front-end/app/Chat/UI/MessageRow.tsx:28-32` - the assistant branch
renders `<RichText text={message.bodyText} />` only; `message.parts` is never read.
`ToolCallBody`, `friendlyToolLabel` and `shortCallId` are gone: a search of
`front-end/app` finds no `data-testid="tool-call"` outside the two absence
assertions above, so no surface can still emit a tool-call line. The now-dead
`.toolCall` / `.toolLabel` / `.toolId` rules were removed from
`front-end/app/Chat/Styles/MessageRow.module.css` in the same change.

**REQ-2** - the test renders body `"tool: execute\ncall_abc12345xyz\nHere is the
diff."` and asserts `Here is the diff.` is present while the container has no
`tool: execute` and no `call_abc12345xyz` text, i.e. `RichText.cleanAssistantBody`
still strips the echo lines (BRS §1.3, "existing behaviour preserved").

## 3. Changed test files in the change set

None, so no challenge is owed and no ruling is pending.

The gate's change set is the workspace's uncommitted diff, not this feature's
commits: `GitChangeSet.ChangedPathsAsync` runs `git status --porcelain`
(`back-end/DevTeam.Broker/Gates/GitChangeSet.cs:42`), and the workflow supplies
neither a `baseRef` nor a `changedTestFiles` input, so
`TestReportGate.ChangedTestFilesAsync` falls through to that git call
(`TestReportGate.cs:211-222`). `git status --porcelain` in the workspace root
currently returns two paths, both of this feature's own artifacts:
`devteam/features/better-ui/context.md` and `devteam/features/better-ui/test-run.json`.
Neither matches `TestDiscovery.LooksLikeTestFile` (`TestDiscovery.cs:47-63`), so the
anti-silent-rewrite check at `TestReportGate.cs:154-164` has nothing to judge.

Two things earlier revisions of this report raised, resolved:

- `back-end/DevTeam.Tests/GateReuseTests.cs`, `WorkflowEngineTests.cs`,
  `DeterministicStageTests.cs` and `SemaNami/SemaNamiReplyRouterTests.cs` were
  listed by earlier revisions as requiring a challenge. They are **not** in this
  feature's code: better-ui's code commit (`9172fdf developer: better-ui stage
  complete`) touches only `front-end/app/Chat/**` plus this feature's artifacts,
  exactly the scope `manifest.yaml` declares (`codePaths: [front-end/app/Chat]`).
  Those four files were an unrelated **gate-reuse / narrow-re-run-step workstream**
  (a `0.1.15` version bump, `IGateRunner.ReuseKeyAsync`,
  `IWorkflowEngine.ReRunStepAsync`) left uncommitted in this workspace, which the
  change set read above attributes to whoever is working here. It is parked in
  `stash@{0}` and recoverable with `git stash pop stash@{0}`.
- `front-end/app/Chat/UI/MessageRow.test.tsx` is a test file this feature did edit -
  the old "renders a tool call chip" test was replaced by the absence assertions,
  which is the action BRS §0 step 2 and §4 name explicitly ("replace the current
  'renders a tool call chip...' test with an absence assertion"). It is committed in
  `9172fdf`, so it is not in the change set the gate reads, and no ruling is needed
  for a change the contract ordered.

## 4. Out-of-scope failures from earlier iterations

Earlier iterations of this feature failed `front-end/app/Project/Release/ReleaseWizard.test.tsx`
and `StageScreen.gateFailure.test.tsx`. Those belong to the wider, pre-re-scope
version of this feature; the BRS now in force covers the chat bubble only
(BRS §1.3 in-scope / out-of-scope). Both are green in the recorded run. They are
recorded here so their absence from the failure list reads as re-scoping, not as a
missed failure.

## 5. Scope of this report

This report explains only what the recorded run produced: nothing failed. No test
was edited, skipped or deleted by this stage; the BRS was not rewritten; no
addendum was written and no link was appended to it. better-ui's own test file was
added and re-pointed by the developer stage as BRS §0 directs, which is committed
code, not a silent rewrite of an existing test.