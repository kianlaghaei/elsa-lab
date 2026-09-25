# ELSA-10 — Process Kill + Restart + Resume

| Metadata | Value |
|---|---|
| Experiment | ELSA-10 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Database | SQL Server 2022 Enterprise, 16.0.1000.6 (RTM) |
| Verified date | 2026-09-25 |
| Implementation commit | [`0a96a5be0e15204c8aa6a7bd98bf8714910bd6d1`](https://github.com/kianlaghaei/elsa-lab/commit/0a96a5be0e15204c8aa6a7bd98bf8714910bd6d1) |

## Observed

This experiment reused the ELSA-07/08 `DocumentReviewBlockingWorkflow` and `WaitForDocumentReviewActivity`. A real child `dotnet` process ran the workflow with `DPC-10-ME-0001`, revision `2`, and review key `discipline-review`; Elsa persisted its bookmark and returned `Running` / `Suspended`. Separate child processes queried the SQL-backed management and bookmark stores. A later child called `IWorkflowResumer` with the exact bookmark ID, and the same workflow instance reached `Finished` / `Finished`. Its bookmark was absent and final named outputs were present in SQL-backed workflow state.

The workflow's persisted bookmark payload supplied the continuation data. The resume call passed an empty input dictionary; this experiment does not revisit ELSA-08's unresolved resume-time `GetInput<T>` behavior.

## Process harness

`tests/ElsaLab.ProcessHarness` is a small executable used by the integration tests. Each invocation creates and starts a real `IHost` with the Elsa SQL Server management and runtime providers, runs normal hosted startup/migration tasks, and registers `WaitForDocumentReviewActivity`. Commands emit one prefixed JSON record to stdout so the parent can capture workflow/bookmark IDs, status, payload values, and the child OS process ID without parsing informational logs. The SQL connection string is passed only through `ELSALAB_SQLSERVER_CONNECTION_STRING`.

The xUnit parent owns a uniquely named SQL database and cleanup. It launches child processes with a 75-second timeout, captures stdout/stderr for failures, and kills any remaining process tree during cleanup. The abrupt-termination test uses a per-test temporary working directory and `Process.Kill(entireProcessTree: true)` after a second process has read the committed suspended state.

## Graceful restart

**Observed:** Process A started and suspended the workflow, reported `Running` / `Suspended`, confirmed the SQL-backed bookmark, and exited normally after stopping its host. Process B was a new PID and performed read-only inspection: the workflow ID, bookmark ID/name/hash, Activity instance ID, metadata, payload fields, and suspended state matched. Process C, also a new PID, resumed the exact bookmark and reloaded `Finished` / `Finished`, zero incidents, final outputs, and no bookmark.

The run captured Process A PID `20116`, inspection Process B PID `3036`, and resume Process C PID `13864`; all differed. Process IDs naturally change on later executions.

## Abrupt process termination

**Observed:** Process A started Elsa, persisted the suspended wait, emitted its IDs, then remained alive waiting on stdin without calling host shutdown. Process B independently loaded the SQL state and bookmark while A was still alive, proving the suspension had reached SQL before termination. The parent then killed A's process tree. A fresh Process C with a different PID loaded and resumed the exact bookmark. The workflow became `Finished` / `Finished`, the bookmark was consumed, final outputs were persisted, and no incidents were present.

The measured PIDs in the verification run were A `27284`, inspection B `14856`, and resume C `10028`. This is an abrupt OS process termination after the suspended state was committed. It does not inject a crash between an arbitrary SQL write and its commit.

## Workflow registration requirement

**Observed:** ELSA-09's manually constructed Host B first queried persisted state, then tried resume before explicitly calling `IWorkflowRegistry.RegisterAsync`; that bare-provider test received a materialization failure. This ELSA-10 harness starts a normal Elsa `IHost`, includes the Activity implementation in the deployed harness, and runs Elsa's hosted startup tasks. In that setup, Process B successfully resumed even though the harness deliberately skipped its explicit `BuildAndRegisterWorkflowAsync` call. A third process repeated the exact resume; Elsa returned no response, kept the row `Finished`, left the bookmark absent, and retained the same output.

This is a real configuration distinction between ELSA-09's bare service-provider construction and ELSA-10's hosted startup. It does **not** prove that an application can omit the Activity implementation from its deployment. In this configuration, the stored definition was available to Elsa's `TypedWorkflowMaterializer`, and the Activity implementation was registered in the process. See also [the ELSA-09 registration observation](ELSA-09-sql-server-persistence.md#code-first-definition-resolution) and the [restart reference](../reference/restart-recovery.md).

## Double restart

**Observed:** The graceful test used three actual processes: A suspended and exited, B inspected the persisted state without consuming the bookmark, and C resumed it. The inspection did not change status, bookmark identity, or payload values. The abrupt test separately proved a held Process A could be killed after the persisted wait was confirmed, then resumed from Process C.

## Multi-instance isolation

**Observed:** One Process A created two suspended workflows with distinct workflow and bookmark IDs (`review-A` and `review-B`) and exited. Process B resumed A only. Its SQL-backed read showed A `Finished` with no bookmark and outputs for document A, while B remained `Running` / `Suspended` with its bookmark and empty output. Process C resumed B independently. The test run captured PIDs A `27084`, B `6408`, and C `22584`.

## SQL state before and after

**Observed:** The harness queried Elsa's configured `IWorkflowInstanceStore` and `IBookmarkStore`, which were backed by the same SQL Server database in each process. Before continuation, a workflow row and bookmark were readable with `Running` / `Suspended`. After continuation, the same workflow ID was readable as `Finished` / `Finished`, its output contained `FinalStatus=ReviewCompleted`, `Finalized=true`, and the document/revision/review key, and the bookmark lookup returned no row. ELSA-09 additionally inspected `ManagementElsaDbContext` and `RuntimeElsaDbContext` directly; ELSA-10 avoids depending on internal table names.

## Bookmark durability

**Observed:** Bookmark ID, name (`DocumentReview`), hash, Activity instance ID, owner workflow ID, metadata, and the logical values `DocumentNumber`, `Revision`, and `ReviewKey` were the same after child-process reconstruction. As in ELSA-09, the serialized payload values survived but were mapped from Elsa's deserialized object representation rather than requiring the original CLR record type. The exact resumed bookmark was consumed, and an additional process-level duplicate resume returned no workflow response and did not re-finalize.

## Process identity evidence

Each JSON response includes `Environment.ProcessId`. Parent tests assert that the suspend, inspect, kill-recovery, and resume steps have different OS PIDs. They also assert Process A is exited normally in the graceful case and is still alive immediately before the parent's `Kill` call in the abrupt case.

## Confirmed from Elsa 3.8.4 source

- `WorkflowResumer` looks up the requested bookmark, reads its owning `WorkflowInstanceId`, and runs that instance with the exact bookmark ID. It does not resume an arbitrary workflow selected only by application input.
- `LocalWorkflowClient` loads a workflow instance and its stored definition version before continuing the persisted state. `WorkflowDefinitionService` selects a materializer by the stored `MaterializerName`; `TypedWorkflowMaterializer` maps the persisted definition to a `Workflow` graph.
- `RecoverInterruptedWorkflowsStartupTask` scans and requeues instances in the `Running` / `Interrupted` substatus. The recurring `RestartInterruptedWorkflowsTask` filters for `IsExecuting=true`, `Running`, and an old `LastUpdated` timestamp. A persisted, non-executing suspended wait was not selected by either path in this test configuration; explicit bookmark resume performed continuation.
- The runtime feature registers registry-population and interrupted-workflow startup tasks. `PopulateRegistriesStartupTask` calls the configured registries populator; `WorkflowDefinitionService` selects a materializer using the stored definition's `MaterializerName`, and `TypedWorkflowMaterializer` maps a stored definition to a workflow graph. This provides a source-supported path consistent with, but does not by itself establish the cause of, the hosted-startup behavior observed above.

These statements are scoped to the tagged source and configured modules below. Startup and recovery settings can differ in another host.

## EDMS implication

A document review that waits for hours or weeks can remain resumable after a planned redeploy or process loss when its suspended workflow and bookmark have already committed to shared SQL persistence. The application should still authorize the reviewer/event, resolve the intended workflow/bookmark, and make external side effects idempotent. This experiment is evidence for persisted suspended-wait recovery in one process at a time; it does not give exactly-once external effects.

## What this does NOT prove

- A process or machine crash during an in-flight Activity or before SQL commit.
- Recovery of an interrupted side effect or automatic retry safety.
- Multi-node ownership, concurrent resume races, distributed locking, or clustered dispatch.
- Workflow-version migration or compatibility after changing Activity code.
- Survival after SQL Server failure or backup/restore.
- That every application hosting mode can resume without its own registration/startup configuration.

## Limitations

The abruptly killed process had already reached a committed suspended state. The tests use SQL Server 2022 and Elsa 3.8.4's EF Core SQL providers in an isolated local test database. No resume-time input was required. The no-explicit-registration result applies to a normally started Elsa host where the persisted typed definition and Activity implementation were available; it is not a blanket deployment rule.

## Executable evidence

- [Process harness](../../tests/ElsaLab.ProcessHarness/Program.cs) — real hosted suspend, inspect, and resume commands.
- [Process restart integration tests](../../tests/ElsaLab.Tests/ElsaProcessRestartTests.cs):
  - `GracefulExit_InspectionSurvivesAnotherProcess_AndResumesInThirdProcess`
  - `AbruptKill_AfterCommittedBookmark_RemainsResumableInFreshProcess`
  - `HostedStartupWithoutExplicitWorkflowRegistration_LoadsPersistedDefinitionAndResumes`
  - `TwoWorkflowInstances_RemainIndependentAcrossSeveralProcesses`
- [ELSA-09 SQL test setup](../../tests/ElsaLab.Tests/ElsaSqlServerPersistenceTests.cs) — isolated SQL database provisioning and store configuration.

Run the process tests with a dedicated SQL Server connection:

```powershell
$env:ELSALAB_SQLSERVER_CONNECTION_STRING = "Server=<sql-server>;Initial Catalog=master;Integrated Security=True;TrustServerCertificate=True;Encrypt=False"
dotnet test tests/ElsaLab.Tests/ElsaLab.Tests.csproj --filter Category=ProcessRestart
```

The parent test creates and drops a unique generated database. Do not point it at a production database.

## Elsa source references

- [`WorkflowResumer.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/WorkflowResumer.cs)
- [`LocalWorkflowClient.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/LocalWorkflowClient.cs)
- [`WorkflowDefinitionService.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Services/WorkflowDefinitionService.cs)
- [`TypedWorkflowMaterializer.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Materializers/TypedWorkflowMaterializer.cs)
- [`RecoverInterruptedWorkflowsStartupTask.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/StartupTasks/RecoverInterruptedWorkflowsStartupTask.cs)
- [`InterruptedRecoveryScanner.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/InterruptedRecoveryScanner.cs)
- [`RestartInterruptedWorkflowsTask.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Tasks/RestartInterruptedWorkflowsTask.cs)
- [`PopulateRegistriesStartupTask.cs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Tasks/PopulateRegistriesStartupTask.cs)
- [ELSA-09 persistence experiment](ELSA-09-sql-server-persistence.md)
