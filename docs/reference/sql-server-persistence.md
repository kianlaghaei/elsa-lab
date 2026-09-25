# SQL Server Persistence

This page summarizes SQL persistence and suspended-workflow recovery exercised by [ELSA-09](../experiments/ELSA-09-sql-server-persistence.md) and [ELSA-10](../experiments/ELSA-10-process-restart-recovery.md). Findings apply to Elsa 3.8.4 on .NET 10 and the tested SQL Server 2022 setup. Process restart of an already-suspended workflow was tested; multi-node recovery was not.

## Package and provider setup

The test references `Elsa.Persistence.EFCore.SqlServer` 3.8.4. That package brings `Microsoft.EntityFrameworkCore.SqlServer` 10.0.9 transitively. Test-only SQL database provisioning uses `Microsoft.Data.SqlClient` 6.1.6.

Elsa management and runtime persistence are separate features and both were configured with `UseEntityFrameworkCore(... UseSqlServer(...))`. The test set `RunMigrations=true` on both configurations.

## Management and runtime records observed

The management `ManagementElsaDbContext` returned `WorkflowInstances` and `WorkflowDefinitions`. The suspended instance row included its workflow state; after continuation it returned the updated finished state and outputs.

The runtime `RuntimeElsaDbContext` returned a `Bookmarks` row while suspended. After resume with `AutoBurn=true`, the row was removed. These typed DbContexts provided database evidence without asserting undocumented table names.

This workflow exercised workflow instance/definition rows and bookmark rows. It did not inventory all runtime record types.

## Suspended state and payload

Across a fresh provider, Elsa returned the same instance and bookmark identifiers, workflow status/substatus, activity execution state, bookmark metadata, and payload values. The bookmark payload's values survived JSON persistence, but the Host B CLR object was `ExpandoObject`, not the original payload record. Map and validate persisted payload fields; do not assume CLR type identity.

The tested wait's callback read the persisted bookmark payload and published final named outputs. The test did not rely on `IWorkflowResumer` resume-time inputs, whose availability through workflow input expressions remains unresolved in the registered code-first path.

## Code-first definition startup

The ELSA-09 test used a manually built service provider and observed a materialization failure before explicitly registering `DocumentReviewBlockingWorkflow` through `IWorkflowRegistry`; registration then enabled resume. ELSA-10 used a normal Elsa `IHost` with the persisted typed definition and the Activity implementation available. Its startup populated Elsa's registries, and resume succeeded even though the harness skipped its explicit registration call. The two observations are scoped to their host setups. Do not conclude that the compiled workflow/Activity implementation can be omitted from the application deployment.

## Process-boundary continuation

ELSA-10 launched a separate OS process for each suspend/inspect/resume phase. After a normal host exit, and after the parent killed a still-running process once SQL already showed the suspended state, another process loaded the same workflow and bookmark from the SQL-backed stores. `IWorkflowResumer` continued the exact bookmark; the workflow row remained with `Finished` / `Finished`, outputs were persisted, and the bookmark was removed. Read-only inspection in an intervening process did not consume the bookmark. Two instances remained isolated across process boundaries.

This verifies recovery only after the suspended workflow and bookmark were committed. It does not test process death during an active Activity, a crash before database commit, multi-node operation, or distributed resume races. See [restart recovery](restart-recovery.md) for the source/runtime distinction and limitations.

## Migrations

In Elsa 3.8.4, `RunMigrations=true` registers `RunMigrationsStartupTask<TDbContext>`, which runs EF Core `Database.MigrateAsync`. A bare test service provider explicitly invoked the management and runtime migration tasks. Elsa runs these tasks during hosted startup.

Automatic migration worked for disposable lab databases. Production should use a reviewed, controlled migration/deployment process rather than have every runtime replica modify schema during normal startup.

## Test database configuration

Set `ELSALAB_SQLSERVER_CONNECTION_STRING` to a test SQL Server connection. The tests create a unique `ElsaLab_<guid>` database, require permissions to create/drop databases, and drop only that generated database at cleanup. No credentials or connection string are committed. Without the variable, SQL integration tests skip; run them explicitly with:

```powershell
dotnet test tests/ElsaLab.Tests/ElsaLab.Tests.csproj --filter Category=SqlServer
```

## Boundaries

- Suspended-workflow process exit/kill and fresh-process resume were tested in ELSA-10. Interrupted active-Activity recovery and multi-node resume remain unverified.
- Elsa workflow/runtime persistence is conceptually separate from EDMS document/revision/task data.
- Elsa state, EDMS database writes, and file/object storage are not established as one ACID transaction by this experiment.
- See [ELSA-09](../experiments/ELSA-09-sql-server-persistence.md) for full observations, exact test evidence, limitations, and tagged source links.
