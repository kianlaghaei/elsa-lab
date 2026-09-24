# Flowchart Cycles

This reference summarizes the one finite revision cycle run in [ELSA-05](../experiments/ELSA-05-revision-cycles.md). It is evidence for that Elsa 3.8.4 workflow, not a general guarantee for persistence, unbounded cycles, or every Flowchart mode.

## Observed cycle

The tested graph was a native `Flowchart` with a `FlowDecision` and an explicit backward `Connection` from `LogRevision` to `MarkReviewing`. `ReviewRound` and `CurrentRevision` were Elsa workflow variables. The decision compared the incremented review round with the caller-supplied `CommentRoundsBeforeApproval`; the selected path either incremented the revision and returned to review or set `ProcessingStatus` to `Approved` and completed.

The workflow was run with `WithTokenBasedFlowchart()`. Zero, two, and one comment rounds resulted in respectively 1, 3, and 2 review executions. This experiment did not run the cyclic graph in counter-based mode.

## Runtime identity and journal

For the three-visit case, `ReviewDocument` kept one Activity object, `Activity.Id`, and `Activity.NodeId`, while Elsa returned three distinct execution-context objects and context IDs. Each execution context reported `ExecutionCount == 1`. `SchedulingActivityExecutionId` showed each return to `MarkReviewing` was scheduled after the prior `LogRevision` execution.

The returned journal had one Flowchart context and repeated child activity contexts. The repeated `FlowDecision` contexts recorded `True`, `True`, and `False` outcomes; only activities on the selected routes appeared. No connection had its own activity execution context. Per-execution `ReviewSummary` values were available in each review context, and the workflow's final output contained the latest summary.

## Mode choice and source context

The 3.8.4 official `ImplicitLoopWorkflowTests` explicitly selects token-based Flowchart execution for a cyclic graph. Elsa's 3.8.4 ADR documents limitations in counter-based scheduling heuristics for loops, XORs, and resumable activities. Counter-based source also contains backward-connection scheduling code, so ELSA-05 does not claim token mode is required; it selects the mode directly demonstrated by the official loop test. No comparative benchmark was performed.

## Evidence

- Runtime observations and limitations: [ELSA-05](../experiments/ELSA-05-revision-cycles.md).
- Flowchart outcome and edge model: [Flowchart routing](flowchart-routing.md).
- Elsa source: [official loop integration test](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/JoinBehaviors/ImplicitLoopWorkflowTests.cs), [`Flowchart.Tokens.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Tokens.cs), [`FlowGraph.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/FlowGraph.cs), and [ADR 0005](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/doc/adr/0005-token-centric-flowchart-execution-model.md).
