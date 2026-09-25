# Restart Recovery for Suspended Workflows

This reference synthesizes the observed process-boundary results in [ELSA-10](../experiments/ELSA-10-process-restart-recovery.md), together with SQL persistence evidence from [ELSA-09](../experiments/ELSA-09-sql-server-persistence.md). It applies to Elsa 3.8.4, .NET 10, and the tested SQL Server 2022 EF Core providers.

## What was proven

- A workflow that reached `Running` / `Suspended` with an Elsa bookmark was readable after its original OS process exited.
- A new process could load the SQL-backed workflow and bookmark, call `IWorkflowResumer.ResumeAsync` for the exact bookmark ID, and persist `Finished` / `Finished`, named outputs, and bookmark consumption.
- This worked after both a graceful host stop and a parent-issued process-tree kill, provided the suspended state had already been confirmed in SQL.
- An inspect-only process did not consume the bookmark or change the suspended state.
- Two suspended instances remained independent across several process boundaries.

## Hosted startup and workflow materialization

In ELSA-09, a manually built service provider that attempted resume before explicitly registering the code-first workflow failed to materialize it. In ELSA-10, a normally started Elsa `IHost` resumed successfully even when the harness skipped its own explicit `IWorkflowRegistry.RegisterAsync` call. The process had the Activity implementation, persistence configuration, and the stored typed workflow definition available. Elsa's `WorkflowDefinitionService` selects a materializer by the persisted `MaterializerName`, and `TypedWorkflowMaterializer` maps a stored definition to a workflow graph. `PopulateRegistriesStartupTask` runs the configured registry population as part of the startup-task mechanism.

Treat these as different tested host configurations, not as conflicting universal rules. A deployed process still needs compatible workflow Activity implementations and Elsa startup configuration. The ELSA-10 test does not show that a missing Activity implementation, altered workflow code, missing materializer, or another hosting model can recover the instance.

## Suspended waits and interrupted execution

In the tagged 3.8.4 source, `RecoverInterruptedWorkflowsStartupTask` invokes a scanner for running instances marked `Interrupted`. The recurring `RestartInterruptedWorkflowsTask` filters for running instances with `IsExecuting=true` whose last update is older than its inactivity threshold. In the tested SQL-hosted setup, the bookmark wait remained `Running` / `Suspended`, was not automatically continued at startup, and required explicit bookmark resume.

The test killed Process A only after another process confirmed the suspended state and bookmark had committed to SQL. It did not test a process death while an Activity was executing or before an update commits. Do not use this result as evidence of interrupted side-effect recovery.

## Application resume path

The harness uses Elsa's `IWorkflowResumer` with the exact `BookmarkId`. The source implementation resolves the stored bookmark and its owner workflow instance before continuing. After successful resume, `AutoBurn=true` removed the bookmark from SQL. A later process repeating the same exact resume got no response, and inspection showed the workflow remained finished with the same output.

Application authorization and duplicate-event handling remain application concerns. Do not expose bookmark IDs as authorization tokens or infer that arbitrary external effects execute exactly once.

## Production boundaries

The verified architecture is:

```text
Elsa management/runtime persistence → workflow instance, execution state, bookmark
EDMS persistence                    → document, revision, task, review data
```

The experiment does not prove multi-node coordination, concurrent duplicate-resume serialization, version migration, active Activity recovery, or atomicity between Elsa state and EDMS/filesystem side effects. Keep external commands idempotent and validate behavior for the deployed host topology.

## Evidence and tagged source

- [ELSA-10 process harness and test cases](../experiments/ELSA-10-process-restart-recovery.md#executable-evidence)
- [`WorkflowResumer.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/WorkflowResumer.cs)
- [`LocalWorkflowClient.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/LocalWorkflowClient.cs)
- [`WorkflowDefinitionService.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Services/WorkflowDefinitionService.cs)
- [`TypedWorkflowMaterializer.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Materializers/TypedWorkflowMaterializer.cs)
- [`RecoverInterruptedWorkflowsStartupTask.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/StartupTasks/RecoverInterruptedWorkflowsStartupTask.cs)
- [`InterruptedRecoveryScanner.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/InterruptedRecoveryScanner.cs)
- [`RestartInterruptedWorkflowsTask.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Tasks/RestartInterruptedWorkflowsTask.cs)
- [`PopulateRegistriesStartupTask.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Tasks/PopulateRegistriesStartupTask.cs)
