# ElsaLab Learnings

## ELSA-01 — Basic Code-First Execution

### What Elsa handled

Elsa executed the two activities in order and reported the workflow as finished. No custom scheduling or execution logic was needed.

### Elsa concepts learned

Used `WorkflowBase`, `IWorkflowBuilder`, `Sequence`, and `WriteLine` to define the workflow; registered it through `AddElsa(...AddWorkflow<...>())`; resolved and called `IWorkflowRunner`; inspected `RunWorkflowResult.WorkflowState.Status`. `ServiceCollection` supplied the host's dependency injection container.

### Runtime behavior

The runner executed synchronously in the console process. It printed `Document received` followed by `Document processing started`, and the returned status was `Finished`. The automated test observed the same finished state through Elsa's runner.

### Surprises

`IWorkflowRunner` is in the `Elsa.Workflows` namespace in the installed 3.8.4 package. `Elsa.Workflows.Core` contains the workflow types but did not provide the documented `AddElsa()` registration extension by itself. The official `Elsa` 3.8.4 bundle provided the bootstrap and aligned Core, Management, Runtime, and API Common package dependencies. The bundle does not add `Elsa.Server.Api` or expose REST endpoints by itself.

### Limitations

This experiment covers only an in-process, synchronous workflow. It does not test persistence, recovery, inputs/outputs, long-running execution, or activity dependency injection.

### Production implications

This confirms that a small, source-controlled workflow can run directly through Elsa's runtime and expose its execution state. It is a useful basis for synchronous orchestration in EDMS/FLOREX. It does not yet establish whether Elsa meets durable or human-driven process requirements; those remain later roadmap experiments.

## ELSA-02 — WorkflowBase, Inputs, Outputs and Variables

### What Elsa handled

The caller passed the document fields through `RunWorkflowOptions.Input`. Elsa evaluated typed workflow input definitions inside native `WriteLine` and `SetVariable<string>` activity inputs. The workflow initialized and changed two native variables, and a later step read the changed values. Elsa returned a completed `WorkflowState`, a named workflow output, and a separate typed workflow result.

### Elsa concepts learned

`IWorkflowBuilder.WithInput<T>(name)` adds an `InputDefinition` with a CLR type and name. It does not create a strongly typed caller argument object: the runner still accepts `IDictionary<string, object>` through `RunWorkflowOptions.Input`.

`IWorkflowBuilder.WithVariable<T>(name, initialValue)` adds a workflow-owned `Variable<T>`. The built-in `SetVariable<T>` takes that variable reference and an `Input<T>` value/expression. `Variable<T>.Get(context)` reads the current value while an activity is executing.

`IWorkflowBuilder.WithOutput<T>(name)` declares a named workflow output. Elsa's built-in `SetOutput` writes that value to the workflow execution output dictionary. `WorkflowBase<TResult>` also has a typed `Result` variable; assigning it produces `RunWorkflowResult<TResult>.Result`.

### Difference between Input, Variable and Output

- **Workflow input definition:** the declared name/type contract for a value supplied when the workflow starts (`WithInput<string>("DocumentNumber")`).
- **Activity input:** an `Input<T>` argument on an activity. In this workflow, input expressions used the matching `InputDefinition` with `ExpressionExecutionContext.GetInput<T>(definition)`.
- **Workflow variable:** a `Variable<T>` owned by the workflow and changed at runtime with Elsa's `SetVariable<T>` activity. It carried `ProcessingStatus` and `StepCount` from one sequence step to the next.
- **Activity output:** an `Output<T>` port exposed by an activity. This example does not use one: the selected built-in activities do not expose a useful activity output for this scenario, and adding a custom activity is outside the experiment.
- **Named workflow output:** an `OutputDefinition` declared with `WithOutput<T>` and assigned here by Elsa's built-in `SetOutput`. It appears in `WorkflowState.Output`.
- **Typed workflow result:** `WorkflowBase<string>.Result` is a separate result variable returned as `RunWorkflowResult<string>.Result`; it is not the named `WorkflowState.Output` dictionary.

### How values are bound between activities

Each workflow input definition is captured by an `Input<T>` expression used by a native activity. `SetVariable<T>` evaluates its `Input<T>` expression and writes the value to the referenced `Variable<T>`. The next `SetVariable<T>` increments `StepCount`, and the later `WriteLine` and result-setting activity read both variables from their expression context. The test verifies the final activity's context and the returned values, so the result does not depend on console text alone.

### How workflow data appears in runtime state

The run returned `WorkflowStatus.Finished`. `WorkflowState.Output["ProcessingStatus"]` contained the named output. `RunWorkflowResult<string>.Result` contained the typed result with the consumed document number, revision, review flag, and final step count.

The three caller values were present in `WorkflowExecutionContext.Input`. `WorkflowState.Input` was empty in this run: `WithInput<T>` created definitions without a storage driver, and Elsa's state extractor only copies inputs whose definitions select a workflow or workflow-instance storage driver. This experiment did not configure one.

Elsa does not expose completed workflow variables as a simple dictionary on `WorkflowState`. After completion, the workflow-level expression context and memory register did not contain these scoped variables by name. The returned `Journal.ActivityExecutionContexts` did retain activity expression contexts; the test reads `ProcessingStatus` and `StepCount` from the final activity context using Elsa's `GetVariable<T>` API.

### Anything surprising in Elsa's API

The workflow has two result paths: named outputs populate `WorkflowState.Output`, while `WorkflowBase<TResult>` populates `RunWorkflowResult<TResult>.Result`. Typed input definitions still require a name-keyed object dictionary at the runner boundary. Also, inspecting scoped variables after completion requires the run journal's activity contexts; querying the root workflow expression context returned `null` for them.

The named output was declared as `string`, but the built-in `SetOutput.OutputValue` property is `Input<object?>`. The output definition documents the expected type, while `WorkflowBase<string>.Result` provides the stronger compile-time result type in this code-first caller.

### Potential EDMS implications

Document number, revision, and review flags can be declared as typed workflow inputs, then copied or transformed into workflow-owned variables as processing proceeds. Named outputs are suitable for values a caller should receive in workflow state, while the typed result is convenient for a code-first caller. Input retention needs an explicit decision: the default definitions used here exposed the values to execution but did not copy them into `WorkflowState.Input`.

### Elsa 3.8.4 source checked

- [`WorkflowBase<TResult>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/WorkflowBase.cs), [`IWorkflowBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowBuilder.cs), and [`WorkflowBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Builders/WorkflowBuilder.cs) for typed results and workflow input/output/variable declarations.
- [`Input<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Input.cs), [`SetVariable<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/SetVariable.cs), and [`SetOutput`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Activities/SetOutput/SetOutput.cs) for binding and assignment signatures.
- [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs), [`WorkflowStateExtractor`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowStateExtractor.cs), [`WorkflowState`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/State/WorkflowState.cs), and Elsa's [activity-output integration test](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/ActivityOutputs) for result/state and activity-output behavior.

## ELSA-03 — Custom Activity + Dependency Injection

### Custom Activity model

`RegisterDocumentActivity` inherits from Elsa's non-generic `CodeActivity`. It overrides `ExecuteAsync(ActivityExecutionContext)` and returns `ValueTask`. `CodeActivity` adds Elsa's auto-complete behavior, which fits this single-call orchestration adapter. It declares three named output ports (`Output<bool>` and `Output<string>`), so the non-generic base keeps those domain-specific port names visible instead of funneling them into one generic `Result` port.

The activity is deliberately thin: it reads its resolved inputs, calls `IDocumentProcessingService`, and copies the service result to Elsa output ports. Validation and reference generation live in `DocumentProcessingService`.

### Dependency injection

The runner registers `IDocumentProcessingService` as scoped and Elsa registers `IWorkflowRunner` as scoped. The console runner and tests create a DI scope, resolve `IWorkflowRunner` from it, and the activity resolves the service with `context.GetRequiredService<IDocumentProcessingService>()`. The activity context delegates service resolution to its workflow execution context, which uses the provider supplied by the runner.

Elsa's custom-activity guidance uses this context service-location pattern because activity definitions need to be constructed and configured as workflow objects. This experiment did not use constructor injection for the application service. A scoped operation service is a suitable fit when a real EDMS service depends on scoped repositories or a unit of work; the workflow runner must be resolved and run within that scope.

### Activity activation/lifetime

The code-first workflow creates `RegisterDocumentActivity` as part of its workflow definition. `ActivityExecutionContext` receives that `IActivity` instance and exposes it as `Activity`; it does not create a new custom activity from DI for every execution. A focused test built one `WorkflowGraph`, ran it twice with different inputs, and observed the same activity object in both journals while getting different registration references.

Treat an activity as definition/configuration data, not as a transient per-run service. Keep values for an execution in local variables, Elsa input/output memory, `ActivityExecutionContext`, or workflow variables. This activity has no mutable per-run fields. Elsa's graph/materialization behavior for other workflow hosting paths was not tested here.

### Activity input binding

The workflow declares `DocumentNumber` and `Revision` with `WithInput<T>`. It assigns the custom activity's `[Input] Input<T>` properties expressions that read those definitions with `ExpressionExecutionContext.GetInput<T>(definition)`. Elsa evaluates wrapped activity inputs before executing the activity. Inside `ExecuteAsync`, the activity reads the resolved values with `DocumentNumber.Get(context)` and `Revision.Get(context)`.

The caller still supplies a name-keyed dictionary through `RunWorkflowOptions.Input`. The test verifies that those exact values reached the custom activity (`GetInputs()`), and a DI fake verifies that the application service received the same document number and revision.

### Activity output binding

The activity exposes `[Output]` ports for `IsValid`, `ProcessingMessage`, and `RegistrationReference`; it assigns each using `Output<T>.Set(context, value)`. Elsa records these values in the activity output register. The next `SetVariable<T>` activities read the named outputs through Elsa's `GetOutput<T>(ActivityExecutionContext, outputName)` accessor, then store them in workflow variables. A later `WriteLine` consumes those variables. The test asserts the output values recorded on the activity execution context and the downstream variable/workflow outputs.

An attempted direct `new Input<T>(activity.OutputPort)` binding faulted in this base `AddElsa` configuration with `Could not find a descriptor for expression type "Output"`. The tagged Elsa integration test reads a previous result with `GetResult<T>`, and the named-port equivalent used here is `GetOutput<T>`. Capturing the outputs in variables also makes the values available to later workflow steps beyond direct activity-output access.

### Cancellation

Elsa's `IWorkflowRunner.RunAsync` accepts a `CancellationToken`; `WorkflowRunner` passes it into `WorkflowExecutionContext`, and `ActivityExecutionContext.CancellationToken` exposes the runtime token to the activity. The activity passes that exact token to `RegisterAsync`. The fake-service test verifies token equality. The sample service checks cancellation before its work; no separate token source is created.

### State safety

The caller values appear in `WorkflowExecutionContext.Input`. Activity inputs and outputs can be inspected in the journal through `ActivityExecutionContext.GetInputs()` and `GetOutputs()`. Workflow variables are visible from the journal's activity expression contexts through `GetVariable<T>`, rather than as a direct dictionary on `WorkflowState`.

The workflow publishes `IsValid`, `ProcessingMessage`, and `RegistrationReference` through `WorkflowState.Output`, and `WorkflowBase<string>.Result` returns the registration reference through `RunWorkflowResult<string>.Result`. As in ELSA-02, these `WithInput<T>` definitions did not populate `WorkflowState.Input` in this experiment. Also, Elsa maps a faulted workflow substatus to the top-level `WorkflowStatus.Finished`; the tests therefore assert both `WorkflowState.Status` and `WorkflowExecutionContext.SubStatus`.

### Testability

The workflow uses only `IDocumentProcessingService`. The test replaces the scoped implementation with a recording fake registered in normal Microsoft DI, without replacing or mocking Elsa runtime services. It verifies service calls and arguments, cancellation propagation, native activity outputs, downstream variable values, and successful workflow state. A separate test uses the real deterministic service.

### EDMS implication

This is an appropriate adapter pattern for `CreateTransmittal`, `ConsolidateComments`, `RegisterRevision`, `SendDocument`, or `ValidateDocument`: give each operation a typed Elsa activity that maps workflow data to an application-service call, and keep the business behavior in that service. This experiment only proves in-process wiring and data flow; it does not implement EDMS persistence or operational guarantees.

### Anything surprising in Elsa's API

`Input<T>` has no parameterless constructor in the installed 3.8.4 package, while `Output<T>` does; the code-first workflow assigns the input ports when it constructs the activity. The workflow's typed result, named workflow outputs, and custom activity outputs are separate paths. A workflow may report top-level `Finished` while its substatus is `Faulted`, so checking only `WorkflowStatus` can hide an activity failure.

### Elsa 3.8.4 source checked

- [`CodeActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/CodeActivity.cs), [`ActivityExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`Input<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Input.cs), and [`Output<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Output.cs) for activity execution, ports, service resolution, and cancellation.
- [`Activity input evaluation`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/ActivityExecutionContextExtensions.InputEvaluation.cs), [`InputExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/InputExtensions.cs), [`OutputExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/OutputExtensions.cs), and [`ActivityExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Extensions/ActivityExtensions.cs) for resolving input values, setting outputs, and reading outputs from a later activity.
- [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs), [`WorkflowsFeature`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Features/WorkflowsFeature.cs), and [`WorkflowGraphBuilder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowGraphBuilder.cs) for runner service lifetime, execution provider/cancellation propagation, and graph construction.
- Elsa's tagged [activity-output integration scenario](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/ActivityOutputs) for its `CodeActivity<T>.Result` and downstream `GetResult<T>` usage. The named outputs in this experiment use the corresponding `GetOutput<T>` accessor.
- Elsa's [custom activities guide](https://docs.elsaworkflows.io/extensibility/custom-activities) describes `CodeActivity`, context-based service resolution, and typed input/output ports. The implementation details above were checked against the installed 3.8.4 source and runtime behavior.
