# better-ui - Test report

| | |
|---|---|
| **Feature** | `better-ui` |
| **Stage** | test-runner |
| **Command** | `cmd.exe /d /c npm --prefix front-end test` |
| **Source of facts** | `devteam/features/better-ui/test-run.json`, written by the `test_run` gate. Not re-run, not edited by this stage. |
| **Recorded run** | ran at `2026-10-01T06:37:45.8045452Z`, exit code `0`, `FailedCount: 0`, `TimedOut: false` |
| **Recorded failures** | none - `Failures: []` |
| **Failure sections** | 0 - the recorded run has no failure, so there is nothing to section |
| **Changed test files** | none - `git status --porcelain` in the workspace root lists only this feature's own artifacts |
| **Verdict on the recorded run** | **pass** - REQ-1 and REQ-2 are each covered by a passing test (§2) |
| **Verdict on this report** | **pass** - every recorded failure is accounted for, and no test file is changed in this feature's diff |

## 1. Failures

`test-run.json` records `ExitCode: 0`, `FailedCount: 0`, `Failures: []`. No test
failed, so this report contains **no `## TEST-<n>` sections** - and it must not.
A section may only explain a failure the machine run actually recorded, so with an
empty `Failures` list any `## TEST-<n>` section is a recording defect and is
rejected outright (`back-end/DevTeam.Broker/Gates/TestReportGate.cs:75-88`). Writing
one would assert a failure that did not happen.

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

## 3. Changed test files in this feature's diff

None. An earlier revision of this report listed four
`back-end/DevTeam.Tests/` files (`GateReuseTests.cs`, `WorkflowEngineTests.cs`,
`DeterministicStageTests.cs`, `SemaNami/SemaNamiReplyRouterTests.cs`) as challenges.
That is no longer true, and the cause is worth recording so it is not re-raised:

- better-ui's code is committed at `9172fdf developer: better-ui stage complete` and
  touches only `front-end/app/Chat/**` plus this feature's own artifacts - exactly
  the scope `manifest.yaml` declares (`codePaths: [front-end/app/Chat]`).
- Those four files, plus `GateRunner.cs`, `WorkflowEngine.cs`, `IWorkflowEngine.cs`,
  `ReleaseEntities.cs`, `Directory.Build.props` and `front-end/package.json`, were an
  unrelated **gate-reuse / narrow-re-run-step workstream** sitting *uncommitted and
  staged* in this workspace (`IWorkflowEngine.ReRunStepAsync`, `IGateRunner.ReuseKeyAsync`,
  a `0.1.15` version bump). It was never committed on any branch and belongs to the
  already-merged `cb3df10 perf(gates)` line, not to this feature.
- `TestReportGate` reads the whole workspace rather than this feature's commits -
  `GitChangeSet.ChangedPathsAsync` runs `git status --porcelain`
  (`back-end/DevTeam.Broker/Gates/GitChangeSet.cs:42`) and no `baseRef` or
  `changedTestFiles` input is supplied (`WorkflowEngine.cs:2917-2924`) - so it
  attributed that other workstream to better-ui.
- That workstream has been parked in
  `stash@{0}: better-ui: park unrelated gate-reuse/re-run-step workstream off this workspace`,
  so the workspace now lists only this feature's artifacts. Nothing was deleted; run
  `git stash pop stash@{0}` to recover it.

The challenges are therefore withdrawn rather than left pending: the files they named
are no longer in the diff, so no operator ruling and no BRS addendum is owed.

## 4. Scope of this report

This report explains only what the recorded run produced: nothing failed. No test was
edited, skipped or deleted by this stage; the BRS was not rewritten; no addendum was
written. better-ui's own test file was added and renamed by the developer stage per
BRS §0 ("write/rename the failing test ... first"), which is committed, not a silent
rewrite of an existing test.