# ELSA-01 — Basic Code-First Execution

| Metadata | Value |
|---|---|
| Experiment | ELSA-01 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c`](https://github.com/kianlaghaei/elsa-lab/commit/b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c) |

## Observed

The code-first `DocumentReceivedWorkflow` derives from `WorkflowBase` and assigns an Elsa `Sequence` to its root. Two `WriteLine` activities ran in order: `Document received`, then `Document processing started`. The runner executed the workflow in-process and returned `WorkflowStatus.Finished`.

The test registered the workflow with `services.AddElsa(elsa => elsa.AddWorkflow<DocumentReceivedWorkflow>())`, resolved `IWorkflowRunner`, called `RunAsync<DocumentReceivedWorkflow>()`, and asserted the returned status. The console run showed both lines; the automated test asserted the status, not captured console output.

## Confirmed from Elsa 3.8.4 source

The tagged source defines the `WorkflowBase`, `Sequence`, `WriteLine`, `IWorkflowRunner`, and `WorkflowRunner` APIs used by this experiment. See [Elsa source references](#elsa-source-references). This experiment did not preserve a source-level inspection of Elsa's `AddElsa` registration extension itself, so the registration/bundle behavior below is recorded as package/runtime observation.

## Inference / Production implication

**Inference from this experiment:** A small source-controlled workflow can run directly through Elsa's in-process runner, making it a useful starting point for synchronous EDMS/FLOREX orchestration. This does not establish suitability for durable, human-driven, or restartable processes.

## Limitations

- Only a short in-process synchronous workflow was tested.
- No workflow inputs, variables, outputs, custom activities, persistence, recovery, or long-running execution were tested here.
- The test asserted top-level `WorkflowStatus.Finished` only. Later experiments added substatus and activity-fault assertions.
- Console text was manually observed, not asserted by the test.

## Executable evidence

At the implementation commit:

- Workflow: [`DocumentReceivedWorkflow.cs`](https://github.com/kianlaghaei/elsa-lab/blob/b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c/src/ElsaLab.Runner/Workflows/DocumentReceivedWorkflow.cs)
- Runner: [`Program.cs`](https://github.com/kianlaghaei/elsa-lab/blob/b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c/src/ElsaLab.Runner/Program.cs)
- Test: [`BasicCodeFirstExecutionTests.cs`](https://github.com/kianlaghaei/elsa-lab/blob/b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c/tests/ElsaLab.Tests/BasicCodeFirstExecutionTests.cs), method `DocumentReceivedWorkflow_FinishesSuccessfully`
- Package target: [`ElsaLab.Runner.csproj`](https://github.com/kianlaghaei/elsa-lab/blob/b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c/src/ElsaLab.Runner/ElsaLab.Runner.csproj)

## Elsa source references

- [`WorkflowBase`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/WorkflowBase.cs)
- [`Sequence`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Sequence.cs) and [`WriteLine`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/WriteLine.cs)
- [`IWorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowRunner.cs) and [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs)

## Historical observations retained

`IWorkflowRunner` is in the `Elsa.Workflows` namespace in the installed 3.8.4 package. `Elsa.Workflows.Core` alone did not provide the `AddElsa()` bootstrap extension used here; the `Elsa` 3.8.4 bundle provided bootstrap and aligned Core, Management, Runtime, and API Common package dependencies. The bundle did not add `Elsa.Server.Api` or expose REST endpoints by itself.
