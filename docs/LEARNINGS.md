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
