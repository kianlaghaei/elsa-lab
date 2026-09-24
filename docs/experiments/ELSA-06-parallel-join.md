# ELSA-06 — Parallel Execution + Join

| Metadata | Value |
|---|---|
| Experiment | ELSA-06 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`47c152954945dce32b75ec3df77443c043653c09`](https://github.com/kianlaghaei/elsa-lab/commit/47c152954945dce32b75ec3df77443c043653c09) |

## Observed

The code-first workflow used Elsa's graph model: `Flowchart` → `FlowFork` → three discipline-specific `ReviewDisciplineActivity` nodes → `FlowJoin` configured with `FlowJoinMode.WaitAll` → `ConsolidateReviewsActivity` → completion. It ran with `.WithTokenBasedFlowchart()`.

Process, Instrument, and Mechanical each ran once and produced a distinct output. The final workflow state exposed all three values and `ConsolidatedStatus=AllReviewsCompleted for DPC-10-ME-0001`. The consolidation activity ran once, received the three output values, and the workflow finished with a finished substatus and no faulted activity contexts.

The controlled asynchronous test held Mechanical at a `TaskCompletionSource` gate. By the time that branch reached its gate, Process and Instrument had completed. `ConsolidateReviews` had not started, and the workflow task was still pending. Releasing Mechanical allowed its completion, the WaitAll join, consolidation, and workflow completion to proceed. The test also measured maximum active review-service calls as one.

In the default in-process runtime used here, the recorded review start order was Process, Instrument, Mechanical, and repeated runs produced the same order. Mechanical is wired last only so the controlled test can hold it after the other two branches finish; this ordering is not a business priority. The controlled async service did not overlap: while one review call awaited, the next scheduled review call had not started. This is a finding about the tested `IWorkflowRunner` host and scheduler, not a claim about distributed dispatch or other hosts.

Each discipline was a separate Activity instance and graph node despite sharing `ReviewDisciplineActivity` type. Each had distinct `Activity.Id`, `NodeId`, and `ActivityExecutionContext.Id`. Their scheduling predecessor was the `FlowFork` execution. The one FlowJoin execution was scheduled after the last branch execution; `ConsolidateReviews` was scheduled after the FlowJoin; and completion followed consolidation. The journal contained one Flowchart, one FlowFork, one FlowJoin, and one consolidation context. Connections were graph data, not activity execution contexts.

`FlowFork` recorded the active `Process`, `Instrument`, and `Mechanical` outcomes in its execution journal. The FlowJoin context exposed `Mode=WaitAll` but had an empty `JournalData` dictionary; it did not provide an arrival counter or a per-branch arrival record. The completed Flowchart context retained a `Flowchart.Tokens` property whose token list was empty after completion.

Branch results were not written concurrently into a shared workflow collection. Each review node set its own `Output<string>`, and after the join the consolidation activity read those three node-specific outputs through Elsa's `GetOutput<T>` accessor. The workflow then published named outputs with `SetOutput`.

## Confirmed from Elsa 3.8.4 source

- Elsa provides both structured `Elsa.Workflows.Activities.Parallel` and graph-based `Flowchart` branching. `Parallel` schedules each child and decrements a scheduled-child count on completion; its parent completes when the count reaches zero.
- `FlowFork` emits named outcomes from its `Branches` input. Flowchart matches those outcome names to connection source ports and schedules active outbound nodes.
- `FlowJoin` is described in source as an explicit join node; regular activities also support merge behavior. Its default is `WaitAny`, so the experiment sets `WaitAll` explicitly.
- The experiment pins token-based execution because Elsa's tagged token-based Flowchart integration tests directly cover fan-out and all-branch convergence, while the tagged FlowJoin tests exercise fork/join under both execution modes. This does not mean token mode is required for this graph.
- In counter-based mode, Flowchart's `FlowJoinMode.WaitAll` path waits until every inbound connection was visited and followed. `WaitAllActive` waits for all inbound connections to be visited and proceeds if at least one was followed.
- In token-based mode, `FlowJoin.Mode=WaitAll` is mapped to `MergeMode.Merge`; token scheduling waits for tokens on all forward inbound connections. `FlowJoin.ExecuteAsync` then completes the join node. `WaitAny` maps to `MergeMode.Race`. The modes therefore use different internal scheduling mechanisms even where this fixed all-branches-active case has the same result.
- The default scheduler factory creates a FIFO `QueueBasedActivityScheduler`. The default workflow scheduler middleware takes a work item and awaits its activity invocation before taking the next item. This code path schedules branches as independent graph work, but does not run their awaited activity bodies concurrently on separate Tasks.
- Elsa records stable graph-node identity separately from activity execution context identity. Each branch execution context carries its own generated `Id` and a `SchedulingActivityExecutionId`; Flowchart token arrival is runtime scheduling state rather than a distinct connection execution journal entry.

## Inference / EDMS implication

Graph fan-out plus an all-branches join is a useful shape for a revision requiring Process, Mechanical, and Instrument reviews before comment consolidation. Branch-specific Activity outputs keep each result separate until consolidation and avoid concurrent writes to a shared mutable list. This is an in-memory synchronous example; it does not prove that discipline reviewers can respond asynchronously or across service instances.

The structured `Parallel` composite may be simpler when the same fixed set of child activities should execute and no explicit graph join is needed. The graph model was selected here because named discipline nodes, branch outcomes, and the explicit WaitAll join are easier to inspect in the returned journal and map directly to the graph-oriented EDMS scenario.

## Limitations

- Only token-based Flowchart execution with three unconditional, forward branches and `FlowJoinMode.WaitAll` was run. The experiment did not compare runtime results under counter-based mode.
- It did not test `WaitAny`, `WaitAllActive`, implicit merge behavior, conditional branch activation, loops mixed with joins, or parallel composite execution.
- The measured one-at-a-time activity invocation applies to the default in-process runner/scheduler path in this test. No external job dispatcher, background scheduling, multi-process execution, or parallel workflow instances were tested.
- Reviews were synchronous in-process application service calls. There was no human waiting, bookmark, persistence, external resume, process restart, or durable review audit.
- The experiment does not prove a human reviewer can answer hours or days later. That requires the later bookmark, blocking, persistence, and external-resume experiments (ELSA-07 through ELSA-10).
- The journal inspection is in-process and ephemeral. The Flowchart token list was empty after completion; no durable join-arrival history was tested.

## Executable evidence

- Flowchart graph and outputs: [DocumentDisciplineReviewWorkflow.cs](../../src/ElsaLab.Runner/Workflows/DocumentDisciplineReviewWorkflow.cs)
- Elsa activities: [ReviewDisciplineActivity.cs](../../src/ElsaLab.Runner/Activities/ReviewDisciplineActivity.cs) and [ConsolidateReviewsActivity.cs](../../src/ElsaLab.Runner/Activities/ConsolidateReviewsActivity.cs)
- Application service: [IDocumentDisciplineReviewService.cs](../../src/ElsaLab.Runner/Services/IDocumentDisciplineReviewService.cs) and [DocumentDisciplineReviewService.cs](../../src/ElsaLab.Runner/Services/DocumentDisciplineReviewService.cs)
- All-branches result, controlled blocked branch, scheduling/identity, journal, and repeated-order assertions: [DocumentDisciplineReviewWorkflowTests.cs](../../tests/ElsaLab.Tests/DocumentDisciplineReviewWorkflowTests.cs)
- Manual execution: [Program.cs](../../src/ElsaLab.Runner/Program.cs)

## Elsa source references

- [`FlowFork`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowFork.cs), [`FlowJoin`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowJoin.cs), and [`FlowJoinMode`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/FlowJoinMode.cs).
- [`Flowchart.Tokens.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Tokens.cs), [`Flowchart.Counters.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Counters.cs), [`FlowJoin merge-mode mapping`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Extensions/ActivityExtensions.cs), and [`MergeMode`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/MergeMode.cs).
- Structured [`Parallel`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Parallel.cs), [`QueueBasedActivityScheduler`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/QueueBasedActivityScheduler.cs), [`ActivitySchedulerFactory`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/ActivitySchedulerFactory.cs), and [`DefaultActivitySchedulerMiddleware`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Middleware/Workflows/DefaultActivitySchedulerMiddleware.cs).
- Tagged [`FlowJoin integration tests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Activities.IntegrationTests/Branching/FlowJoinTests.cs), [`token-based Flowchart tests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Activities.IntegrationTests/Flow/FlowchartTokenBasedTests.cs), and [`Parallel tests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Activities.IntegrationTests/ParallelTests.cs).
