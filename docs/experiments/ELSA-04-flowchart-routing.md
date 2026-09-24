# ELSA-04 — Flowchart + Conditional Routing

| Metadata | Value |
|---|---|
| Experiment | ELSA-04 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`24f7d2554b9259806896b9e36c94c62241769f9b`](https://github.com/kianlaghaei/elsa-lab/commit/24f7d2554b9259806896b9e36c94c62241769f9b) |

## Observed

The code-first root is Elsa's `Flowchart`, with an explicit `Start`, `Activities`, and `Connection`s. The workflow input `RequiresReview` is evaluated by `FlowDecision`; its `True` and `False` outcomes route to named `SetVariable<string>` activities.

With `RequiresReview=true`, `ReviewDocument` sets `ProcessingStatus` to `Reviewed`. With `false`, `AutoAcceptDocument` sets it to `AutoAccepted`. Both paths connect to the shared `CompleteDocument` Sequence. Each route's test observed that sequence once; it published named workflow outputs and assigned the typed workflow result.

The workflow pins counter-based execution per run with `WithCounterBasedFlowchart()`. The condition is constructed as `new FlowDecision(context => context.GetInput<bool>(requiresReviewInput))`, so it reads the workflow input through Elsa's expression context when the decision executes.

## Confirmed from Elsa 3.8.4 source

`Connection` contains source and target `Endpoint`s; an endpoint can have an optional port name. `Connection` has no Boolean condition property. Flowchart matches a completed activity's outcome names to outbound connection source ports. Ordinary activities in this graph use the `Done` outcome; `FlowDecision` emits `True` or `False`.

In counter-based mode, an ordinary target with multiple inbound connections uses an implicit `WaitAllActive` merge. Elsa tracks inbound connection visits and schedules the target when the active inbound path arrives and alternatives have been accounted for. The tagged `FlowDecision` integration tests also exercise mutually exclusive branches converging on one activity.

Other source-confirmed options not implemented here include `FlowSwitch` (labeled cases and `Default`), custom named outcomes using `[FlowNode(...)]` and `CompleteActivityWithOutcomesAsync`, token-based Flowchart execution, and explicit `FlowJoin` merge modes. A condition predicate directly on `Connection` is not part of the 3.8.4 model.

## Routing approach selected

**Observed and selected:** `FlowDecision` plus outcome-labeled connections. It is appropriate for the Boolean `RequiresReview` input and provides the named `True`/`False` source ports needed by this graph. A `FlowSwitch` would fit a multi-value classification; a custom activity outcome would make sense if the decision itself needed custom domain logic or named outcomes.

## Branches and shared completion

**Observed:** the service registration Activity and its named outputs are captured into workflow variables before routing. The selected branch updates the shared `ProcessingStatus` variable. Both outcomes converge on one ordinary completion Sequence; no explicit `FlowJoin` is used. The common Sequence publishes `IsValid`, `ProcessingMessage`, `RegistrationReference`, and `ProcessingStatus`, then sets the typed result to the branch-specific status.

## Runtime journal and state

**Observed:** the journal contains a completed Flowchart execution context and a `FlowDecision` context. The decision's `JournalData["Outcomes"]` is a `string[]` containing only `True` or only `False`, matching the caller input. The selected branch appears in the journal; the unselected branch has no activity execution context. `CompleteDocument` appears once. There is no separate per-connection journal entry in the returned activity-context list; the outcome and selected target activity evidence the selected edge.

`WorkflowState.Output` contains all four named outputs, and the typed workflow result contains the branch status. Both route tests assert `WorkflowStatus.Finished`, `WorkflowSubStatus.Finished`, and no faulted activity contexts.

## Sequence vs Flowchart

### Sequence

**Observed across ELSA-01 through ELSA-03:** `Sequence` ran activities in declared order, making an unbranched path such as `A → B → C` compact. ELSA-04 still uses a Sequence for the shared completion steps.

### Flowchart

**Observed in ELSA-04:** Flowchart schedules activities through graph connections and activity outcomes. The journal makes the decision outcome and executed branch inspectable, while the unused branch is absent from activity execution contexts. The graph required explicit node and connection declarations that a linear Sequence did not.

**Inference for future EDMS use:** Sequence is a compact choice for an unbranched operation list. Flowchart can make named branches and convergence visible in code, at the cost of explicit graph wiring.

## Inference / Production implication

**Inferred, not implemented:** Boolean rules such as `RequiresClientApproval?`, `RequiresVendorResponse?`, `HasComments?`, and `RevisionIsValid?` could map to `FlowDecision` outcomes and connections. Multi-category rules may fit `FlowSwitch`. Multi-discipline review could require more complex behavior, but parallel execution and joins are outside this experiment.

## Limitations

- This experiment tested counter-based routing only. Elsa's token-based mode was confirmed in source/tests but not run here.
- It tested one Boolean decision and one exclusive merge; it did not test nested decisions, multiple joins, loops, parallel paths, or domain-specific custom outcomes.
- It did not test durable execution or branch behavior after persistence/restart.
- Journal observations are from the returned in-process run result, not a persisted audit store.

## Anything surprising in Elsa's API

**Observed:** the branch outcome is recorded in the activity context's `JournalData`, not as a connection execution context. Tests combine the outcome with branch activity presence/absence and the shared continuation to establish routing.

**Confirmed in source:** Flowchart has counter-based and token-based execution modes, with merge behavior that may vary by mode or explicit `FlowJoin`. This experiment explicitly selects counter-based execution instead of relying on an implicit default. A top-level `WorkflowStatus.Finished` alone remains insufficient evidence of success; the tests also check substatus and activity faults.

## Executable evidence

At the implementation commit:

- Workflow: [`DocumentProcessingWorkflow.cs`](https://github.com/kianlaghaei/elsa-lab/blob/24f7d2554b9259806896b9e36c94c62241769f9b/src/ElsaLab.Runner/Workflows/DocumentProcessingWorkflow.cs)
- Runner: [`Program.cs`](https://github.com/kianlaghaei/elsa-lab/blob/24f7d2554b9259806896b9e36c94c62241769f9b/src/ElsaLab.Runner/Program.cs)
- Tests: [`DocumentProcessingWorkflowTests.cs`](https://github.com/kianlaghaei/elsa-lab/blob/24f7d2554b9259806896b9e36c94c62241769f9b/tests/ElsaLab.Tests/DocumentProcessingWorkflowTests.cs), methods `RequiresReviewTrue_RoutesToReviewAndRejoinsCompletion` and `RequiresReviewFalse_RoutesToFastPathAndRejoinsCompletion`. The file also retains DI/cancellation and activity-reuse evidence from ELSA-03.

## Elsa source references

- [`Flowchart`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.cs), [`Connection`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/Connection.cs), and [`Endpoint`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Models/Endpoint.cs).
- [`FlowDecision`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowDecision.cs), [`FlowNodeAttribute`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Attributes/FlowNodeAttribute.cs), [`FlowSwitch`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowSwitch.cs), and [`FlowJoin`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/FlowJoin.cs).
- [`Flowchart counter execution and implicit merge`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Activities/Flowchart.Counters.cs), [`run-option mode selection`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Flowchart/Extensions/RunWorkflowOptionsExtensions.cs), and [`outcome recording on completion`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.Complete.cs).
- Tagged Elsa [`FlowDecision integration tests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Activities.IntegrationTests/Branching/FlowDecisionTests.cs) for outcome-based routing and branch convergence under counter-based and token-based execution.
