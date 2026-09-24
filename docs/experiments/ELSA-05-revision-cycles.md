# ELSA-05 — Loops and Revision Cycles

| Metadata | Value |
|---|---|
| Experiment | ELSA-05 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | Recorded as the feature commit in the follow-up documentation commit |

## Observed

`DocumentRevisionWorkflow` ran one native Elsa `Flowchart` in explicitly selected token-based mode using `.WithTokenBasedFlowchart()`. The caller supplied `DocumentNumber`, `InitialRevision`, and `CommentRoundsBeforeApproval` through `RunWorkflowOptions.Input`. Elsa evaluated `FlowDecision` against the workflow input and a `ReviewRound` workflow variable. When `HasComments` returned `True`, the graph followed the backward connection from `LogRevision` to `MarkReviewing`; `False` followed the approval path to `CompleteDocument`.

The three runtime cases produced:

| Starting revision | Comment rounds before approval | Reviews | Revision increments | Final revision | Review rounds | Final status |
|---:|---:|---:|---:|---:|---:|---|
| 0 | 0 | 1 | 0 | 0 | 1 | Approved |
| 0 | 2 | 3 | 2 | 2 | 3 | Approved |
| 5 | 1 | 2 | 1 | 6 | 2 | Approved |

Every case ended with `WorkflowStatus.Finished`, `WorkflowSubStatus.Finished`, a completed Flowchart context, one `CompleteDocument` execution, and no faulted activity execution contexts.

In the two-cycle journal, `ReviewDocument` appeared three times and `HasComments` recorded `True`, `True`, then `False` in `JournalData["Outcomes"]`. The comments/revision path appeared twice and the approval path once. There was one Flowchart context, not one per trip around the cycle. No activity context represented a connection itself. The runtime graph retained the explicit `LogRevision -> MarkReviewing` connection, and the journal's `SchedulingActivityExecutionId` linked each later `MarkReviewing` execution to the preceding `LogRevision` execution.

The same `ReviewDocumentActivity` object was used for each visit: its object reference, `Activity.Id`, and `Activity.NodeId` stayed the same. Each visit had a distinct `ActivityExecutionContext` object and `Id`; each newly created context reported `ExecutionCount == 1`. This is the observed distinction between a graph definition node and its runtime executions in this test.

Each review context retained its own resolved inputs and `ReviewSummary` output. A downstream `SetVariable` captured that iteration's output, and the final named workflow output resolved to the last summary (`Review round 3 — Revision 2`). The journal therefore retained each iteration's output even though the workflow-level latest-output lookup supplied the last value to the final output.

## Confirmed from Elsa 3.8.4 source

- Elsa exposes token-based and counter-based Flowchart execution. `WithTokenBasedFlowchart()` selects token-based execution through run options; this experiment pins that choice rather than relying on a default.
- The official 3.8.4 `ImplicitLoopWorkflowTests` integration test builds a cyclic Flowchart and explicitly selects `.WithTokenBasedFlowchart()`.
- The 3.8.4 Flowchart design record explains that counter-based scheduling heuristics had trouble with loops, XORs, and resumable activities. The counter-based scheduler remains in the source and contains backward-connection scheduling logic; this experiment does not establish that a cycle requires token mode.
- Elsa's graph analysis distinguishes forward and backward connections and validates backward edges against paths back to the target. Token execution schedules child activities from outcome-matching connections and tracks tokens in Flowchart execution context properties.
- Elsa creates an `ActivityExecutionContext` when scheduling an activity execution. That context has its own generated `Id`; it refers to an `IActivity` whose graph `Id` and `NodeId` belong to the workflow definition.
- `ActivityOutputRegister` records activity outputs with both stable activity identity and activity-execution identity. Source lookup by activity definition resolves the last recorded output, while an execution-specific lookup can identify a particular execution's output.

## Inference / EDMS implication

This observed pattern is a candidate for a synchronous revision cycle: keep the current revision, review round, and current status in Elsa workflow variables; route back through the same review node while comments remain; leave the cycle through an approval outcome. Similar rule inputs could include `HasComments` or a future domain-derived “comments remain” value. The experiment does not establish how to persist or audit such history durably.

For an EDMS audit trail, the stable activity node identifies the kind of step, while each context ID distinguishes a particular execution. The journal evidence suggests that both identifiers may be useful for troubleshooting and review-round history, but durable audit suitability remains untested.

## Limitations

- Only token-based Flowchart execution was run. The official integration test and source motivated this choice; counter-based cyclic runtime behavior was not compared or measured.
- This is a deterministic in-memory loop. It does not wait for an external party, persist state, survive a process restart, or resume from a bookmark/event.
- No parallel review, join, retry, timer, versioning, or human task behavior was tested.
- The tests cover three small finite inputs. They do not test extremely long or unbounded cycles or broader runaway-execution safeguards.
- Review output history was inspected through the in-process activity journal and final output. It is not a persistent audit store.

## Executable evidence

- Workflow graph and outputs: [DocumentRevisionWorkflow.cs](../../src/ElsaLab.Runner/Workflows/DocumentRevisionWorkflow.cs)
- Repeated output activity: [ReviewDocumentActivity.cs](../../src/ElsaLab.Runner/Activities/ReviewDocumentActivity.cs)
- Zero cycles, two cycles, non-zero start, journal identity, route counts, final outputs, and clean completion assertions: [DocumentRevisionWorkflowTests.cs](../../tests/ElsaLab.Tests/DocumentRevisionWorkflowTests.cs)
- Manual token-based example: [Program.cs](../../src/ElsaLab.Runner/Program.cs)

## Elsa source references

- Official cyclic runtime test and workflow: [`ImplicitLoopWorkflowTests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/JoinBehaviors/ImplicitLoopWorkflowTests.cs) and [`ImplicitLoopWorkflow`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/JoinBehaviors/ImplicitLoopWorkflow.cs).
- [`Flowchart token execution`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Tokens.cs), [`counter execution`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Counters.cs), and [`Flowchart`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.cs).
- [`FlowGraph` forward/backward edge analysis](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/FlowGraph.cs), [`Flowchart execution modes`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/FlowchartExecutionMode.cs), and [`run-option mode selection`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Extensions/RunWorkflowOptionsExtensions.cs).
- [`ActivityExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`WorkflowExecutionContext` context creation](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/WorkflowExecutionContext.cs), [`ActivityOutputRegister`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/ActivityOutputRegister.cs), and [`ActivityOutputRecord`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/ActivityOutputRecord.cs).
- [ADR 0005: token-centric Flowchart execution](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/doc/adr/0005-token-centric-flowchart-execution-model.md).
