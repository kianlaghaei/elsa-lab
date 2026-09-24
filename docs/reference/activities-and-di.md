# Activities and Dependency Injection

This guide synthesizes the in-process behavior demonstrated in [ELSA-03](../experiments/ELSA-03-custom-activity-di.md). Scope is limited to the code-first Activity and graph path covered by its tests.

## Activity shape used

`RegisterDocumentActivity` inherits from non-generic `CodeActivity` and overrides `ExecuteAsync(ActivityExecutionContext)`. It has two typed `[Input] Input<T>` ports and three named `[Output] Output<T>` ports. This shape was chosen because the activity exposes multiple named results and performs one service call. ELSA-03 did not compare all possible Activity base classes, so it does not establish that `CodeActivity` is always the right base.

## Binding and execution

The workflow binds input expressions using `ExpressionExecutionContext.GetInput<T>(definition)`. At execution time the Activity reads resolved values with `Input<T>.Get(context)`. It writes results through `Output<T>.Set(context, value)`. The tested downstream path reads named outputs with `GetOutput<T>(activityExecutionContext, outputName)` and carries them forward in workflow variables. See the [workflow data reference](workflow-data.md).

## Application service resolution

The runner registers `IDocumentProcessingService` as a normal scoped .NET service. `RegisterDocumentActivity` resolves it from `ActivityExecutionContext` with `context.GetRequiredService<IDocumentProcessingService>()`; constructor injection was not used in this experiment. The runner itself is resolved and run inside the same DI scope, which allows a scoped service to be used in the tested in-process flow.

The Activity maps inputs to a service call and maps the returned service result to Elsa output ports. Validation and reference generation are implemented in the ordinary service. See the [activity/application-service pattern](../patterns/activity-application-service.md).

## Activation and per-execution state

The workflow definition constructed the Activity. In the test that built one `WorkflowGraph` and ran it twice, both journals referred to the same Activity object. That observation means this graph path did not create a fresh Activity object for each workflow run. Keep per-run mutable values out of Activity instance fields; put them in local execution variables, Elsa inputs/outputs, `ActivityExecutionContext`, or workflow variables.

The test does not prove how every Elsa host/materialization path activates Activities. The safe rule in this lab is based on the reuse that was actually observed, and avoids relying on fresh-instance behavior.

## Cancellation

`IWorkflowRunner.RunAsync` accepted a cancellation token; the Activity used `ActivityExecutionContext.CancellationToken` and passed that same token to the application service. The fake service test asserted token equality. It did not exercise cancellation of a long-running external operation or document a persisted cancellation lifecycle.

## Testability

The workflow depends on `IDocumentProcessingService`. A test replaced only that service using normal Microsoft DI and left the actual Elsa runtime active. It asserted the service call, input values, returned native Activity outputs, later workflow data, token propagation, and completion state. The tested pattern allows the application service implementation to be substituted without a custom workflow wrapper.

## Verified evidence and source

- Runtime observations and limitations: [ELSA-03](../experiments/ELSA-03-custom-activity-di.md).
- Executable implementation at the experiment commit: [Activity](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Activities/RegisterDocumentActivity.cs), [workflow](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Workflows/DocumentProcessingWorkflow.cs), and [tests](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/tests/ElsaLab.Tests/DocumentProcessingWorkflowTests.cs).
- Elsa 3.8.4 source: [`CodeActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/CodeActivity.cs), [`ActivityExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`Input<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Input.cs), and [`Output<T>`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Output.cs).
