# ELSA-09 — SQL Server Persistence

| Metadata | Value |
|---|---|
| Experiment | ELSA-09 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Database | SQL Server 2022 Enterprise, 16.0.1000.6 (RTM) |
| Verified date | 2026-09-24 |
| Implementation commit | [`817c3fbc62da4e573bb6722ab1a265008a604dd8`](https://github.com/kianlaghaei/elsa-lab/commit/817c3fbc62da4e573bb6722ab1a265008a604dd8) |

## Observed

The experiment reuses `DocumentReviewBlockingWorkflow` and `WaitForDocumentReviewActivity` from ELSA-07/08. Host A started the document review with `DPC-10-ME-0001`, revision `2`, and review key `discipline-review`. The existing wait Activity created a `DocumentReview` bookmark and left the workflow at `WorkflowStatus.Running` / `WorkflowSubStatus.Suspended`.

Host A's management store returned the suspended `WorkflowInstance` and a saved code-first workflow definition row. Its runtime bookmark store returned the bookmark. The same values were independently queried through Elsa's management and runtime EF Core DbContexts: one workflow instance and definition in `ManagementElsaDbContext`, and one bookmark in `RuntimeElsaDbContext`. The bookmark's workflow ID, bookmark ID/name/hash, Activity instance ID, metadata, and payload identity were retained.

Host A's service provider and scope were disposed before Host B was created. Host B used a new `ServiceCollection`, `ServiceProvider`, database contexts, and Elsa stores against the same database. It loaded the suspended workflow and bookmark by scalar IDs. Host B then resumed the exact bookmark with `IWorkflowResumer`. The final persisted state was `WorkflowStatus.Finished` / `WorkflowSubStatus.Finished`, with no incidents and no bookmarks. Elsa's runtime database no longer contained the consumed bookmark row.

The second integration test created two suspended instances in Host A, disposed that provider, and loaded both in Host B. Resuming A finished A and consumed only A's bookmark while B remained `Running` / `Suspended` with its bookmark and empty output. B then resumed independently and produced its own document/revision/review-key outputs.

## SQL Server configuration

The test uses `Elsa.Persistence.EFCore.SqlServer` 3.8.4. `Microsoft.EntityFrameworkCore.SqlServer` 10.0.9 is available transitively from that package; the test does not add a direct EF SQL Server reference. `Microsoft.Data.SqlClient` 6.1.6 is a direct test dependency used to create and remove isolated databases.

Set `ELSALAB_SQLSERVER_CONNECTION_STRING` to a SQL Server login that can create and drop databases. The tests create uniquely named `ElsaLab_<guid>` databases and drop only those generated databases in cleanup. No connection string is stored in the repository. Without the variable, the two SQL integration tests are skipped; when it is set, ordinary `dotnet test` includes them.

For example, configure the variable in the shell using your own local or dedicated test-server settings, then run:

```powershell
dotnet test tests/ElsaLab.Tests/ElsaLab.Tests.csproj --filter Category=SqlServer
```

Both Elsa persistence sides were configured:

```csharp
elsa.UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef =>
{
    ef.UseSqlServer(connectionString);
    ef.RunMigrations = true;
}));

elsa.UseWorkflowRuntime(runtime => runtime.UseEntityFrameworkCore(ef =>
{
    ef.UseSqlServer(connectionString);
    ef.RunMigrations = true;
}));
```

The test host is a bare `ServiceProvider`, so it resolves Elsa's registered `RunMigrationsStartupTask<ManagementElsaDbContext>` and `RunMigrationsStartupTask<RuntimeElsaDbContext>` and invokes both. In a hosted application Elsa runs startup tasks as part of host startup. Elsa 3.8.4's task calls EF Core `Database.MigrateAsync`. Automatic migration is convenient for this isolated lab database. For production, apply reviewed migrations through a controlled deployment step rather than allowing every application replica to mutate the schema during ordinary startup.

## Management persistence

**Observed:** `ManagementElsaDbContext` queried the suspended `WorkflowInstances` row and the saved `WorkflowDefinitions` version row. The instance row returned the workflow status/substatus and serialized `WorkflowState`, including activity execution state and its bookmark snapshot. After Host B resumed, the same instance row reported `Finished` / `Finished` and the final named outputs.

The test does not claim every management record type or every possible definition source was exercised. The code-first row existed, but its existence alone was insufficient to reconstruct the compiled Activity graph in a new provider (see [code-first registration behavior](#code-first-definition-resolution)).

## Runtime persistence

**Observed:** `RuntimeElsaDbContext.Bookmarks` contained the pending bookmark after suspension and Host B reconstruction. After successful resume with the bookmark's default `AutoBurn=true`, the bookmark was absent from both `IBookmarkStore` and the SQL-backed `Bookmarks` DbSet. The test queried Elsa's typed DbContexts rather than depending on undocumented SQL table names.

The runtime records inspected in this experiment were bookmarks. It does not assert that every possible runtime/inbox/execution record type is required or persisted by this workflow.

## Suspended-state durability

Host B independently loaded the workflow as `Running` / `Suspended`, with the same workflow instance ID and an empty workflow output dictionary, before resume. The persisted wait Activity state included `DocumentNumber`, `Revision`, and `ReviewKey`; the bookmark retained its identity and document correlation data. Host B needed no Elsa object, scope, provider, graph, bookmark, or `WorkflowState` object retained from Host A.

This establishes persistence across disposal and reconstruction of the application DI/runtime graph in one process. It is not a process-kill, crash, machine-restart, or multi-node recovery test.

## Bookmark persistence

The bookmark's Host B fields matched the Host A identifiers: `Id`, `Name` (`DocumentReview`), `Hash`, `WorkflowInstanceId`, `ActivityInstanceId`, and metadata `ReviewKey`. Its payload preserved `DocumentNumber`, `Revision`, and `ReviewKey` values.

The payload did **not** round-trip as `DocumentReviewBookmarkPayload`. Its Host B runtime CLR type was `System.Dynamic.ExpandoObject`; its serialized property values were accessible as `documentNumber`, `revision`, and `reviewKey`. The Activity's optional ELSA-09 resume callback reads those values from the persisted bookmark and writes named outputs before completing the wait. This is evidence about this Elsa 3.8.4 JSON configuration, not a promise that arbitrary application CLR types retain their CLR identity across persistence.

## Workflow-state round trip

Before resume in Host B, the workflow output dictionary was empty and `WorkflowState.Input` was not used as the continuation channel. The bookmark payload carried the original document/revision/review-key tuple. After Host B resumed, the callback copied those values into named outputs `FinalDocumentNumber`, `FinalRevision`, and `FinalReviewKey`; the downstream finalization Sequence added `FinalStatus="ReviewCompleted"` and `Finalized=true`. The completed state was reloaded from the SQL-backed `IWorkflowInstanceStore` and its outputs and empty incidents were asserted.

The ELSA-08 finding that values supplied to the `IWorkflowResumer` input dictionary were not available through downstream workflow `GetInput<T>` expressions remains unresolved. ELSA-09 passes an empty resume dictionary and does not depend on resume-time input. It proves continuation data survives in the bookmark payload, not that `WorkflowState.Input` itself is serialized or rehydrated.

## Host A → Host B continuation

1. Host A builds and registers the code-first workflow, starts it, and persists the suspended state and bookmark.
2. Host A's scope and service provider are disposed.
3. Host B creates a fresh service collection/provider against the same SQL database and queries the persisted state.
4. Host B registers the compiled workflow definition through Elsa's `IWorkflowRegistry`.
5. Host B's `IWorkflowResumer.ResumeAsync(bookmarkId, emptyInput, cancellationToken)` resumes the persisted wait.
6. The bookmark is burned, the Activity callback publishes its saved correlation values, finalization runs, and the finished state is reloaded from SQL.

The test has two Host A/Host B scenarios: one detailed payload/state round trip and one two-instance isolation run.

## Code-first definition resolution

**Observed:** A workflow-definition row could be queried from Host B before registration. Calling the resumer at that point threw `NullReferenceException` while the new runtime attempted to materialize the workflow graph; the workflow remained suspended and its bookmark remained present. Building the compiled `DocumentReviewBlockingWorkflow` and registering it through the native `IWorkflowRegistry` on Host B made the subsequent exact-bookmark resume succeed.

For this compiled code-first path, persistable definition metadata does not replace registering the workflow implementation on application startup. The test records this behavior for the configured runtime/materializer path; it does not establish requirements for Elsa's separately hosted or dynamically authored definition scenarios.

## Confirmed from Elsa 3.8.4 source

- The SQL Server persistence extensions configure the EF Core provider separately for Workflow Management and Workflow Runtime. Both were enabled in this test.
- `ManagementElsaDbContext` exposes workflow definitions and workflow instances; `RuntimeElsaDbContext` exposes bookmarks.
- The runtime commit path persists workflow execution state, variables, and bookmarks through Elsa's commit-state handling. The SQL test observed the instance state and bookmark rows through provider APIs.
- `RunMigrations=true` registers the EF Core migration startup task; the Elsa 3.8.4 task invokes EF Core migrations for its context.
- Elsa's EF bookmark store persists serialized bookmark payload and metadata. The observed Host B payload CLR type was confirmed by the runtime test as `ExpandoObject`.
- Elsa's workflow registry/populator supplies registered workflow definitions to runtime materialization. Registering the compiled code-first workflow on Host B was required in this test.

## EDMS implication

ELSA persistence stores Elsa workflow/runtime state; the EDMS database remains responsible for documents, revisions, review tasks, comments, transmittals, and storage metadata. They may share a SQL Server installation or be deployed separately, but they represent different responsibilities.

The bookmark payload is a useful small correlation/continuation carrier in this test. Production payloads should be deliberately versionable and limited to values needed to continue or resolve application records. The test's `ExpandoObject` round trip is a reason to validate and map payload fields rather than depend on the original CLR record type after rehydration.

ELSA-09 does not make Elsa's state and EDMS data, database, or file/object storage one ACID transaction. Application-side idempotency and reconciliation remain important for side effects, as separately observed in [EDMS-FIT-01](../fit-tests/EDMS-FIT-01-document-storage.md).

## Limitations

- The experiment disposed and reconstructed service providers inside one live process. It did not kill/restart the process, reboot a machine, or test distributed/multi-node ownership. Those remain ELSA-10 and later work.
- It used one local SQL Server 2022 instance and one SQL database per test. Other SQL Server versions/topologies were not tested.
- The test used Elsa's automatic migrations against disposable lab databases. It does not recommend automatic schema migration from every production replica.
- Code-first workflow registration is required in the tested Host B runtime path.
- Resume-time input transport through `IWorkflowResumer` remains unresolved; the test does not rely on it.
- The persisted bookmark payload rehydrated to `ExpandoObject`, not the original CLR type.
- The test does not persist EDMS domain tables, filesystem changes, or distributed transactions.

## Executable evidence

- [SQL Server persistence tests](../../tests/ElsaLab.Tests/ElsaSqlServerPersistenceTests.cs): `SuspendedInstanceAndBookmark_PersistAcrossFreshProviders_AndResumeFromSql` and `TwoPersistedSuspendedInstances_ResumeIndependentlyFromFreshProvider`.
- [Blocking Activity](../../src/ElsaLab.Runner/Activities/WaitForDocumentReviewActivity.cs) and [workflow](../../src/ElsaLab.Runner/Workflows/DocumentReviewBlockingWorkflow.cs), reused from ELSA-07/08 and minimally extended with an optional resume callback that reads persisted bookmark data.
- Tests create a UUID-named database and remove only that generated database at cleanup. `ELSALAB_SQLSERVER_CONNECTION_STRING` is required to run them; without it, the two `[Trait("Category", "SqlServer")]` cases skip.

## Elsa source references

- Tagged [`SqlServerProvidersExtensions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore.SqlServer/SqlServerProvidersExtensions.cs) and [`PersistenceFeatureBase`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore.Common/PersistenceFeatureBase.cs).
- Tagged management [`DbContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore/Modules/Management/DbContext.cs) and [`WorkflowInstanceStore`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore/Modules/Management/WorkflowInstanceStore.cs).
- Tagged runtime [`DbContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore/Modules/Runtime/DbContext.cs) and [`BookmarkStore`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore/Modules/Runtime/BookmarkStore.cs).
- Tagged [`RunMigrationsStartupTask`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Persistence.EFCore.Common/RunMigrationsStartupTask.cs) and [`WorkflowDefinitionMapper`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Mappers/WorkflowDefinitionMapper.cs).
- Tagged [`DefaultWorkflowRegistry`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/DefaultWorkflowRegistry.cs), [`DefaultWorkflowDefinitionStorePopulator`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/DefaultWorkflowDefinitionStorePopulator.cs), and [`TypedWorkflowMaterializer`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Materializers/TypedWorkflowMaterializer.cs).
- Tagged [`JsonPayloadSerializer`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Serialization/Serializers/JsonPayloadSerializer.cs), [`PolymorphicObjectConverter`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Serialization/Converters/PolymorphicObjectConverter.cs), and [`DefaultCommitStateHandler`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/DefaultCommitStateHandler.cs).
