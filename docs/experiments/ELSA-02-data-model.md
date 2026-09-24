# ELSA-02 — WorkflowBase, Inputs, Outputs and Variables

| Metadata | Value |
|---|---|
| Experiment | ELSA-02 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`e4517859c1567b16c4bdfd4a330b62c194fcbc27`](https://github.com/kianlaghaei/elsa-lab/commit/e4517859c1567b16c4bdfd4a330b62c194fcbc27) |

## Observed

The caller passed `DocumentNumber`, `Revision`, and `RequiresReview` through `RunWorkflowOptions.Input`. Elsa evaluated those workflow inputs inside native `WriteLine` and `SetVariable<string>` activity inputs. The workflow initialized and changed native `ProcessingStatus` and `StepCount` variables; later activities observed the updated values. Elsa returned a finished `WorkflowState`, a named workflow output, and a separate typed workflow result.

The test observed caller values in `WorkflowExecutionContext.Input`. `WorkflowState.Input` was empty. `WorkflowState.Output["ProcessingStatus"]` held the named output, and `RunWorkflowResult<string>.Result` held `Processed DPC-10-ME-0001 revision 2; RequiresReview=True; StepCount=2`. The test inspected `ProcessingStatus` and `StepCount` through the final journal activity's `ExpressionExecutionContext.GetVariable<T>`.

## Elsa concepts learned

- `IWorkflowBuilder.WithInput<T>(name)` adds a typed `InputDefinition`. It does not create a strongly typed caller argument object; the runner still accepts a name-keyed `IDictionary<string, object>` in `RunWorkflowOptions.Input`.
- `IWorkflowBuilder.WithVariable<T>(name, initialValue)` adds a workflow-owned `Variable<T>`. Built-in `SetVariable<T>` accepts that variable reference and an `Input<T>` expression/value. `Variable<T>.Get(context)` reads its current value while an activity executes.
- `IWorkflowBuilder.WithOutput<T>(name)` declares a named workflow output. Elsa's built-in `SetOutput` assigns it to the workflow execution output dictionary.
- `WorkflowBase<TResult>.Result` is another workflow-owned value. Assigning it produces `RunWorkflowResult<TResult>.Result`; it is separate from named outputs.

## Difference between Input, Variable and Output

- **Workflow input definition:** a declared name/type for a value supplied when the workflow starts, such as `WithInput<string>("DocumentNumber")`.
- **Activity input:** an `Input<T>` argument on an activity. This workflow assigned expressions that read the workflow input definition with `ExpressionExecutionContext.GetInput<T>(definition)`.
- **Workflow variable:** a `Variable<T>` owned by the workflow and changed with `SetVariable<T>`. Here, `ProcessingStatus` and `StepCount` carried values between sequential activities.
- **Activity output:** an `Output<T>` port exposed by an activity. ELSA-02 did not use one; adding a custom activity was outside that experiment.
- **Named workflow output:** an output definition declared with `WithOutput<T>` and assigned with `SetOutput`. It appears in `WorkflowState.Output`.
- **Typed workflow result:** `WorkflowBase<string>.Result` appears as `RunWorkflowResult<string>.Result`; it is not the named `WorkflowState.Output` dictionary.

## How values are bound between activities

Each input definition is captured by an `Input<T>` expression supplied to a native activity. `SetVariable<T>` evaluates its `Input<T>` and writes the value to the referenced `Variable<T>`. The next step increments `StepCount`; later `WriteLine` and result-setting activities read the changed variables. The test asserts the returned values and final activity context, not just console output.

## How workflow data appears in runtime state

**Observed:** the caller values were present in `WorkflowExecutionContext.Input`; the declared inputs did not populate `WorkflowState.Input` in this run. `WithInput<T>` created definitions without a storage driver, and the state extractor only copies inputs whose definitions select a workflow or workflow-instance storage driver. No such driver was configured in this experiment.

**Observed:** completed workflow variables were not exposed as a simple dictionary on `WorkflowState`. After completion, the root workflow expression context and memory register did not contain the scoped variables by name. `Journal.ActivityExecutionContexts` retained activity expression contexts, and the test read the variables from the final activity context with Elsa's `GetVariable<T>` API.

## Confirmed from Elsa 3.8.4 source

The `WorkflowBase<TResult>`, builder, input, variable, and output APIs and the state-extraction behavior were checked against the tagged sources below. The built-in `SetOutput.OutputValue` is `Input<object?>`, even when the declared output definition has a narrower type. The output definition records the intended type; `WorkflowBase<string>.Result` provided the stronger compile-time result type in this caller.

## Inference / Production implication

**Inference:** document number, revision, and review flags can be typed workflow inputs and then copied or transformed into workflow-owned variables as processing proceeds. Named outputs suit values a caller should receive in workflow state; the typed result is convenient for code-first callers. Whether inputs should be retained in state requires an explicit storage decision.

## Limitations

- This experiment used a linear `Sequence`; routing was left for ELSA-04.
- No custom activity output was introduced; that was tested in ELSA-03.
- It did not configure input storage drivers or persistence.
- It did not establish how variables are exposed through other hosts or durable workflow state.

## Executable evidence

At the implementation commit:

- Workflow: [`DocumentProcessingWorkflow.cs`](https://github.com/kianlaghaei/elsa-lab/blob/e4517859c1567b16c4bdfd4a330b62c194fcbc27/src/ElsaLab.Runner/Workflows/DocumentProcessingWorkflow.cs)
- Runner: [`Program.cs`](https://github.com/kianlaghaei/elsa-lab/blob/e4517859c1567b16c4bdfd4a330b62c194fcbc27/src/ElsaLab.Runner/Program.cs)
- Test: [`DocumentProcessingWorkflowTests.cs`](https://github.com/kianlaghaei/elsa-lab/blob/e4517859c1567b16c4bdfd4a330b62c194fcbc27/tests/ElsaLab.Tests/DocumentProcessingWorkflowTests.cs), method `DocumentProcessingWorkflow_ConsumesInputs_UpdatesVariables_AndReturnsResult`

## Elsa source references

- [`WorkflowBase<TResult>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/WorkflowBase.cs), [`IWorkflowBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowBuilder.cs), and [`WorkflowBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Builders/WorkflowBuilder.cs) for results and input/output/variable declarations.
- [`Input<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Input.cs), [`SetVariable<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/SetVariable.cs), and [`SetOutput`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Activities/SetOutput/SetOutput.cs) for binding and assignment.
- [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs), [`WorkflowStateExtractor`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowStateExtractor.cs), and [`WorkflowState`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/State/WorkflowState.cs) for state/result behavior.
- Elsa's tagged [activity-output integration scenario](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/ActivityOutputs) demonstrates `CodeActivity<T>.Result` and downstream `GetResult<T>` usage; ELSA-03 tested named outputs and `GetOutput<T>`.
