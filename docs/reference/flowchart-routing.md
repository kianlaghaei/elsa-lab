# Flowchart Routing

This reference summarizes the one Boolean, exclusive-routing graph executed in [ELSA-04](../experiments/ELSA-04-flowchart-routing.md). It does not describe untested loop or parallel semantics.

## Tested graph model

The code-first workflow uses `Flowchart` with an explicit `Start`, `Activities`, and `Connections`. Each `Connection` links source and target `Endpoint`s; source endpoint port names match activity outcomes. For this graph, normal nodes use `Done`, and `FlowDecision` emits `True` or `False`.

The Boolean condition reads the workflow's `RequiresReview` input using an Elsa expression. `true` runs the review status activity; `false` runs the auto-accept status activity. Both routes connect to one shared completion Sequence.

## Selected approach and alternatives

ELSA-04 selected built-in `FlowDecision` plus outcome-labeled connections. In Elsa 3.8.4 a `Connection` is not itself a Boolean conditional edge. Source-confirmed alternatives include:

- `FlowSwitch` for labeled value cases and a `Default` outcome.
- Custom outcomes declared with `[FlowNode(...)]` and completed with `CompleteActivityWithOutcomesAsync`.
- Token-based Flowchart execution as another execution mode.
- Explicit `FlowJoin` when a graph requires a specified merge mode.

Only counter-based execution was selected and tested. The test run pins it with `WithCounterBasedFlowchart()`.

## Merge and journal behavior

In the tested counter-based mode, Elsa allowed both mutually exclusive outcomes to connect to an ordinary shared target without a `FlowJoin`. Elsa 3.8.4 source describes the implicit `WaitAllActive` handling for such a target; the tagged FlowDecision integration tests also exercise branch convergence.

At runtime, the journal included a completed Flowchart context and a `FlowDecision` context. `JournalData["Outcomes"]` recorded the selected outcome. The selected branch activity appeared and the skipped branch activity did not; the common continuation appeared once. No separate connection execution context appeared in the returned activity-context list. These are observations for this test and execution mode.

## Evidence and source

- Runtime details and limitations: [ELSA-04](../experiments/ELSA-04-flowchart-routing.md).
- Code/tests at the experiment commit: [workflow](https://github.com/kianlaghaei/elsa-lab/blob/24f7d2554b9259806896b9e36c94c62241769f9b/src/ElsaLab.Runner/Workflows/DocumentProcessingWorkflow.cs), [tests](https://github.com/kianlaghaei/elsa-lab/blob/24f7d2554b9259806896b9e36c94c62241769f9b/tests/ElsaLab.Tests/DocumentProcessingWorkflowTests.cs).
- Elsa 3.8.4 tagged sources: [`Flowchart`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.cs), [`Connection`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/Connection.cs), [`FlowDecision`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowDecision.cs), and [`Flowchart counter execution`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Counters.cs).
