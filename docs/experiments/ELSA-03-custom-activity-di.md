# ELSA-03 — Custom Activity + Dependency Injection

| Metadata | Value |
|---|---|
| Experiment | ELSA-03 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`bf50d68bfde14ba0a51a73a5ff134b4bc878148c`](https://github.com/kianlaghaei/elsa-lab/commit/bf50d68bfde14ba0a51a73a5ff134b4bc878148c) |

## Observed

`RegisterDocumentActivity` inherits Elsa's non-generic `CodeActivity`. It has typed `Input<string>` and `Input<int>` ports and three named output ports (`Output<bool>` and `Output<string>`). `ExecuteAsync(ActivityExecutionContext)` reads resolved inputs, calls `IDocumentProcessingService`, and copies the result to Elsa output ports. Validation and reference generation are in `DocumentProcessingService`, not the activity.

The runner registers `IDocumentProcessingService` as scoped, registers the activity/workflow with `AddElsa`, creates a DI scope, resolves `IWorkflowRunner` from that scope, and executes the workflow. The Activity resolves the application service with `context.GetRequiredService<IDocumentProcessingService>()`; this experiment did not use constructor injection.

The workflow binds its `DocumentNumber` and `Revision` inputs with expressions that call `ExpressionExecutionContext.GetInput<T>(definition)`. Elsa evaluates the wrapped activity inputs before execution; the Activity reads resolved values with `DocumentNumber.Get(context)` and `Revision.Get(context)`. Its outputs are assigned with `Output<T>.Set(context, value)`. Later `SetVariable<T>` activities read them using `GetOutput<T>(ActivityExecutionContext, outputName)`, then a later step consumes the workflow variables.

The caller supplies a name-keyed dictionary through `RunWorkflowOptions.Input`. Tests verified the exact values reached the activity and the service. A recording fake was substituted through normal Microsoft DI while the real Elsa runtime remained in use. The fake test verified service arguments, cancellation-token equality, activity outputs, downstream data, and successful state.

## Custom Activity model

**Observed:** the experiment used non-generic `CodeActivity` and overrode `ExecuteAsync(ActivityExecutionContext)`. The activity exposes three independently named output ports; the non-generic base fits that shape without funneling them into a single generic result port. It provides Elsa's auto-complete behavior appropriate to this one-call adapter.

**Limit:** this experiment did not compare `CodeActivity`, `CodeActivity<T>`, and `Activity` across other execution patterns. The choice is justified for this activity's named ports and single-call behavior, not a universal rule.

## Dependency injection

`ActivityExecutionContext` resolves services through its workflow execution context and the provider supplied by the runner. Elsa's custom-activity guidance uses context service resolution for activity definitions configured as workflow objects. The code registers the normal .NET service as scoped and resolves/runs `IWorkflowRunner` inside the matching scope.

For a real EDMS service that depends on scoped repositories or a unit of work, scoped lifetime is a reasonable fit for this arrangement. This experiment only verifies the in-process scope used here; it does not prove lifetime behavior for every host or workflow execution path.

## Activity activation/lifetime

**Observed:** the code-first workflow creates `RegisterDocumentActivity` as part of its definition. `ActivityExecutionContext.Activity` referred to that `IActivity` instance; Elsa did not create a new Activity from DI for every run in this test. A test built one `WorkflowGraph`, ran it twice, and observed the same activity object in both journals while receiving distinct run data and references.

Do not treat the Activity object as a transient per-execution service. Keep per-run values in local variables, Elsa input/output memory, `ActivityExecutionContext`, or workflow variables. The activity has no mutable per-run fields. The reuse test covers the graph-building path it exercised; Elsa's activity materialization in other hosting paths was not tested.

## Activity input binding

The workflow declares `DocumentNumber` and `Revision` with `WithInput<T>`, then assigns the custom Activity's `[Input] Input<T>` properties expressions that read those definitions using `ExpressionExecutionContext.GetInput<T>(definition)`. At execution time, the Activity uses `Input<T>.Get(context)` to read evaluated values.

## Activity output binding

The activity exposes `[Output]` ports and assigns them with `Output<T>.Set(context, value)`. Elsa records the values in the activity output register. In this experiment, downstream workflow activities retrieve the named outputs with `GetOutput<T>(ActivityExecutionContext, outputName)` and copy them into workflow variables. This made the values available to a later step.

An attempted direct binding using `new Input<T>(activity.OutputPort)` faulted under this experiment's `AddElsa` configuration with `Could not find a descriptor for expression type "Output"`. Elsa's tagged integration scenario reads a prior result through `GetResult<T>`; the named-port accessor used here is `GetOutput<T>`. This observation is specific to the installed package/configuration tested.

## Cancellation

`IWorkflowRunner.RunAsync` accepts a `CancellationToken`. Elsa passed it to `WorkflowExecutionContext`, and `ActivityExecutionContext.CancellationToken` exposed the runtime token. The activity passed that exact token to `RegisterAsync`; the fake-service test verified equality. The sample service checks cancellation before doing work. No separate token source is created by the Activity.

## State safety

The caller values appeared in `WorkflowExecutionContext.Input`. Activity inputs and outputs were inspectable in journal contexts through `GetInputs()` and `GetOutputs()`. The workflow exposed named values through `WorkflowState.Output`; workflow variables were inspected through a journal activity expression context and `GetVariable<T>`, rather than as a direct dictionary on `WorkflowState`.

The workflow publishes `IsValid`, `ProcessingMessage`, and `RegistrationReference` through `WorkflowState.Output`. Its `WorkflowBase<string>.Result` separately returns the registration reference through `RunWorkflowResult<string>.Result`. The experiment also observed that top-level `WorkflowStatus.Finished` can coexist with a faulted substatus. Tests therefore check both `WorkflowState.Status` and `WorkflowExecutionContext.SubStatus`, as well as activity faults.

## Testability

The workflow depends on `IDocumentProcessingService`. The test registered a fake implementation through normal Microsoft DI without mocking Elsa runtime internals. It verified the service call, arguments, cancellation propagation, native activity outputs, downstream values, and successful workflow state. Another test used the deterministic service implementation.

## Inference / Production implication

**Inference:** this thin adapter shape is appropriate to explore operations such as `CreateTransmittal`, `ConsolidateComments`, `RegisterRevision`, `SendDocument`, or `ValidateDocument`: map workflow values to a typed Activity call, and keep business rules in normal application services. This experiment proves only in-process wiring and data flow, not EDMS persistence, transactions, idempotency, or operational guarantees.

## Limitations

- Only the in-process runner and one built graph reuse path were tested.
- Constructor injection was not used or compared; the Activity uses `ActivityExecutionContext.GetRequiredService<T>()`.
- The service implementation is deterministic and in-memory; no external system or database was exercised.
- The direct Activity output-port-to-`Input<T>` binding attempt failed in this base configuration; the experiment used named `GetOutput<T>` followed by variables.
- Cancellation propagation was tested with token identity, not with a long-running operation or observed cancellation/fault lifecycle.

## Anything surprising in Elsa's API

In the installed 3.8.4 package, `Input<T>` has no parameterless constructor while `Output<T>` does. The code-first workflow assigns input ports when constructing the Activity. The custom Activity outputs, named workflow outputs, and typed `WorkflowBase<TResult>.Result` are separate paths. A top-level `Finished` status can hide a faulted substatus.

## Executable evidence

At the implementation commit:

- Activity: [`RegisterDocumentActivity.cs`](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Activities/RegisterDocumentActivity.cs)
- Workflow: [`DocumentProcessingWorkflow.cs`](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Workflows/DocumentProcessingWorkflow.cs)
- Service: [`IDocumentProcessingService.cs`](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Services/IDocumentProcessingService.cs) and [`DocumentProcessingService.cs`](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Services/DocumentProcessingService.cs)
- Tests: [`DocumentProcessingWorkflowTests.cs`](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/tests/ElsaLab.Tests/DocumentProcessingWorkflowTests.cs), methods `DocumentProcessingWorkflow_UsesRegisteredService_AndFinishes`, `DocumentProcessingWorkflow_UsesFakeServiceThroughDi_AndPassesOutputToLaterSteps`, and `ReusingWorkflowGraph_ReusesActivityDefinitionInstance_AndKeepsRunDataSeparate`

## Elsa source references

- [`CodeActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/CodeActivity.cs), [`ActivityExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`Input<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Input.cs), and [`Output<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Output.cs).
- [`Activity input evaluation`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/ActivityExecutionContextExtensions.InputEvaluation.cs), [`InputExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/InputExtensions.cs), [`OutputExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/OutputExtensions.cs), and [`ActivityExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/ActivityExtensions.cs).
- [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs), [`WorkflowsFeature`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Features/WorkflowsFeature.cs), and [`WorkflowGraphBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowGraphBuilder.cs) for runner lifetime, execution provider/cancellation propagation, and graph construction.
- Elsa's tagged [activity-output integration scenario](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/ActivityOutputs) and [custom activities guide](https://docs.elsaworkflows.io/extensibility/custom-activities). Implementation details above were checked against the installed 3.8.4 source and runtime.
