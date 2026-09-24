# Workflow Data

This reference synthesizes behavior observed in [ELSA-02](../experiments/ELSA-02-data-model.md) and [ELSA-03](../experiments/ELSA-03-custom-activity-di.md), plus the branch result in [ELSA-04](../experiments/ELSA-04-flowchart-routing.md). It describes the Elsa 3.8.4 in-process code-first tests, not every possible host or storage configuration.

## Data paths

| Concept | Elsa API observed | Where it appears / how it is read |
|---|---|---|
| Caller workflow input | `RunWorkflowOptions.Input` name-keyed dictionary | `WorkflowExecutionContext.Input` at runtime; workflow expressions use the corresponding input definition |
| Workflow input definition | `IWorkflowBuilder.WithInput<T>(name)` | Typed definition; read through `ExpressionExecutionContext.GetInput<T>(definition)` |
| Activity input | `[Input] Input<T>` | Bound by a value or expression; the activity reads the resolved value through `Input<T>.Get(ActivityExecutionContext)` |
| Workflow variable | `WithVariable<T>` and `Variable<T>` | Elsa-owned mutable data; `SetVariable<T>` updates it and `Variable<T>.Get(context)` reads it in later activity expressions |
| Activity output | `[Output] Output<T>` | `Output<T>.Set(context, value)` records the value in the activity output register; downstream tested access uses `GetOutput<T>(activityExecutionContext, name)` |
| Named workflow output | `WithOutput<T>(name)` and `SetOutput` | Returned in `WorkflowState.Output` |
| Typed workflow result | `WorkflowBase<TResult>.Result` | Returned as `RunWorkflowResult<TResult>.Result`, separate from named workflow outputs |

## Input binding

`WithInput<T>` declares a typed workflow input, but the tested runner boundary remains a dictionary keyed by input name. For custom Activity input properties, the code-first workflow assigned `Input<T>` expressions that call `GetInput<T>(definition)`. Elsa evaluated those expressions before executing the Activity; the Activity then read its resolved values with `Input<T>.Get(context)`. The decision condition in ELSA-04 used the same workflow input expression pattern.

In ELSA-02 and ELSA-03, caller data appeared in `WorkflowExecutionContext.Input` but not in `WorkflowState.Input`. Those input definitions did not select a storage driver, and no input storage driver was configured. Do not treat these as the same state field; decide input retention explicitly for another setup.

## Variables between activities

`WithVariable<T>(name, initialValue)` declared workflow-owned variables. The native `SetVariable<T>` activity updated them, and subsequent activity expressions read the current values. Variables were not exposed as a simple root dictionary on the returned `WorkflowState` in ELSA-02. That test inspected them through a retained journal activity context's `ExpressionExecutionContext.GetVariable<T>`.

## Activity outputs and workflow outputs

Activity outputs, named workflow outputs, and typed workflow results are separate channels:

- A custom Activity sets its output ports with `Output<T>.Set(context, value)`. ELSA-03 copied the named output values into workflow variables using `GetOutput<T>` in a downstream activity.
- A workflow declares caller-visible named outputs with `WithOutput<T>` and assigns them with Elsa's `SetOutput`. ELSA-02/03/04 observed those in `WorkflowState.Output`.
- A code-first caller may also use `WorkflowBase<TResult>.Result`, returned via `RunWorkflowResult<TResult>.Result`. In these examples it did not stand in for `WorkflowState.Output`.

ELSA-03 tried direct `Input<T>` binding from an Activity output port and got `Could not find a descriptor for expression type "Output"` with that `AddElsa` configuration. The tested named-port path used `GetOutput<T>` and workflow variables. Treat this as the observed behavior of that setup, not a universal prohibition on every Elsa binding mechanism.

## Runtime state and journal

The verified facts above come from in-process `IWorkflowRunner` results. The workflow state exposes its named output dictionary; the run result separately exposes the typed result and execution context. Activity input/output registers and scoped variable values were inspected through `Journal.ActivityExecutionContexts` and Elsa accessors. Completed workflow variables were not directly available as a simple dictionary on `WorkflowState` in the tested setup.

Tests in ELSA-03 and ELSA-04 check `WorkflowState.Status` and `WorkflowExecutionContext.SubStatus`; top-level `WorkflowStatus.Finished` alone can coexist with an undesirable faulted substatus. The relevant test additionally checks for faulted activity contexts.

## Limits of this reference

No durable/persisted state provider, alternate host, input storage driver, or process restart was exercised. Do not infer persistence behavior from the in-process journal or returned state.

## Evidence and Elsa source

- Experiment evidence: [ELSA-02](../experiments/ELSA-02-data-model.md), [ELSA-03](../experiments/ELSA-03-custom-activity-di.md), and [ELSA-04](../experiments/ELSA-04-flowchart-routing.md).
- Tagged sources: [`IWorkflowBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowBuilder.cs), [`Input<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Input.cs), [`Output<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Output.cs), [`SetVariable<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/SetVariable.cs), [`SetOutput`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Activities/SetOutput/SetOutput.cs), and [`WorkflowStateExtractor`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowStateExtractor.cs).
