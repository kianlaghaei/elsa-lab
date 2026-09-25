# ELSA-13 — Workflow Definition Versioning

| Metadata | Value |
|---|---|
| Experiment | ELSA-13 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (net10.0) |
| Database | SQL Server 2022 |
| Verified date | 2026-09-25 |
| Implementation commit | Pending feature commit |

## Observed

- Two code-first Elsa WorkflowBase definitions were registered with one logical definition ID, EngineeringReview, but distinct definition-version IDs (EngineeringReview-v1 and EngineeringReview-v2) and integer versions 1 and 2.
- SQL-backed ManagementElsaDbContext stored both rows. The V1 suspended WorkflowInstance referenced DefinitionId EngineeringReview, DefinitionVersionId EngineeringReview-v1, and Version 1. The bookmark retained the same ID, hash, ActivityInstanceId, and owning workflow ID when V2 was registered and published.
- A V2 draft was marked IsLatest=true and IsPublished=false. A Published query and a new workflow start through the Published selector still selected V1, while Latest selected the draft V2. A test start using the exact V2 version ID could run the draft deliberately.
- Publishing V2 made V2 both latest and published and changed V1 to neither latest nor published. A new workflow started through WorkflowDefinitionHandle.ByDefinitionId(EngineeringReview, VersionOptions.Published) used V2. In the same provider, the registered-definition cache resolved V2 after publication.
- The old V1 bookmark resumed after V2 publication. Its instance retained DefinitionVersionId EngineeringReview-v1 and Version 1, completed with DefinitionMarker=V1, and did not execute the V2-only coordinator step. A V2 instance completed with DefinitionMarker=V2 and CoordinatorExecuted=true.
- Four concurrent suspended instances (two per version) were resumed in mixed order. Each kept its original definition-version ID and emitted its own version marker.
- A separate Process A/Process B integration test used SQL as the continuity boundary and different OS process IDs. Process A suspended V1 and exited. Process B registered V1 and V2, started a new published V2 instance, then resumed the original V1 instance as V1.
- Retraction was tested while V1 was published and V2 was only a draft. Retracting V1 made the Published selector return no definition, but left the definition row, suspended instance, and bookmark intact; the instance still resumed as V1. Once V2 had been published and V1 was already unpublished, attempting to retract V1 was rejected.
- Deleting V1 with an active instance was tested against a dedicated generated database. Elsa deleted the V1 definition version, the active V1 workflow instance, and its bookmark. V2 remained. Deletion therefore is destructive to active work in this tested configuration.
- In a fresh host, registering a changed code-first graph with the same DefinitionId, version ID, and version number replaced the stored V1 graph. The old suspended instance then finalized with V1-MUTATED. Elsa 3.8.4 did not protect a published code-first version from same-identity mutation.
- When the V1 finalization Activity implementation was unavailable, Elsa materialized the stored graph with Elsa.NotFoundActivity. Resuming faulted the instance as Finished/Faulted and consumed its AutoBurn bookmark. A second untouched V1 instance remained resumable. Restoring the old Activity implementation in another fresh host allowed the stored V1 graph to resume even though DocumentReviewVersion1Workflow itself was not built or registered in that host.
- SQL assertions inspected ManagementElsaDbContext.WorkflowDefinitions and WorkflowInstances directly. The test observed both version rows, latest/published flags, and the instance's exact definition-version reference before completion.

## Elsa definition identity model

Observed for this code-first SQL Server setup:

- DefinitionId is the logical workflow ID shared by versions.
- WorkflowDefinition.Id is the persisted ID of one concrete version. The instance field DefinitionVersionId points to this ID.
- Version is Elsa's integer version number for that logical definition.
- WorkflowInstance.Id is the running instance ID. The instance also stores DefinitionId, DefinitionVersionId, and Version.
- IsLatest and IsPublished are independent flags. A latest draft can coexist with an earlier published version.

Do not conflate these values with a document revision, review cycle, or application release. For example, DocumentRevision=R3, ReviewCycle=2, WorkflowDefinitionVersion=5, and ApplicationVersion=1.7.0 describe different things.

## Latest and Published semantics

The tests used WorkflowDefinitionHandle.ByDefinitionId(logicalId, VersionOptions.Published) for a normal new-instance selection and WorkflowDefinitionHandle.ByDefinitionVersionId(versionId) for deliberate exact-version/draft selection. After V2 publication, the former resolved V2; before publishing the draft, it resolved V1. This is the tested production-oriented selection pattern for this host.

Elsa's IWorkflowDefinitionPublisher.PublishAsync published V2 and updated both version flags as described above. Retraction removed V1 from published selection without deleting the stored version. Deletion was a separate operation and removed dependent running state in the tested SQL provider.

## Elsa APIs exercised

- IWorkflowBuilder.WithDefinitionId, IWorkflowBuilder.WithId, and the builder Version property assign the shared logical ID, version-row ID, and integer version for the typed code-first definitions.
- IWorkflowBuilderFactory.BuildWorkflowAsync and IWorkflowRegistry.RegisterAsync build and register each compiled WorkflowBase graph. WorkflowPublication represents a latest draft in the draft-selection test.
- IWorkflowDefinitionPublisher.PublishAsync and RetractAsync exercise publish/retract behavior. IWorkflowDefinitionManager.DeleteVersionAsync exercises destructive version deletion.
- WorkflowDefinitionHandle.ByDefinitionId with VersionOptions.Published selects new production starts; ByDefinitionVersionId deliberately starts a specific draft/version.
- IWorkflowRuntime.CreateClientAsync starts new instances. IWorkflowResumer.ResumeAsync resumes the exact bookmark for an existing instance.
- IWorkflowDefinitionStore, IWorkflowInstanceStore, IBookmarkStore, and ManagementElsaDbContext inspect stored version and instance state.
## Code-first versioning model

The code-first definitions set the shared logical identity with IWorkflowBuilder.WithDefinitionId, and set their concrete IDs with IWorkflowBuilder.WithId; builder.Version sets the integer version. Each compiled workflow is built by IWorkflowBuilderFactory and registered through IWorkflowRegistry.RegisterAsync. The test sets Workflow.Publication to LatestDraft for the draft case, then uses IWorkflowDefinitionPublisher.PublishAsync.

The startup registration path stores/materializes a workflow graph for a version. A fresh process that needs to start or resume these workflows registers compatible definitions and Activities again. The stored typed graph allowed one V1 continuation without rebuilding the V1 WorkflowBase class, provided the Activity implementation referenced by the stored graph was registered.

## V1 suspended instance

The main V1 instance suspended at WaitForDocumentReviewActivity with Status=Running and SubStatus=Suspended. Its WorkflowInstance.DefinitionVersionId matched the V1 WorkflowDefinition.Id and its Version was 1. SQL and bookmark-store reads confirmed this before and after V2 deployment.

## V2 deployment and new-instance selection

V2 added CoordinatorCheckActivity and FinalizeVersion2Activity. The marker and CoordinatorExecuted output made the selected graph observable. Publishing V2 allowed new starts through the Published selector to use V2; registering V2 as a draft did not change that Published selection. The same-provider cache and a separate process startup both observed V2 after publication/registration.

## V1 continuation after V2

The V1 bookmark was resumed by its exact Elsa bookmark ID through IWorkflowResumer. The same workflow instance completed on version 1, with V1 marker, no coordinator output, and a consumed bookmark. V2's addition did not rewrite the instance's DefinitionVersionId or bookmark identity.

## Process restart

The Process A / Process B test used different OS PIDs and one generated SQL Server database. Process A registered and ran V1 to suspension and exited. Process B created a new host, registered V1 and V2, selected Published for a new instance (V2), and resumed the saved V1 bookmark. The old instance finished as V1. This proves process-boundary version behavior only after the SQL commit and while compatible Activity code is deployed.

## Old implementation availability

The experiment distinguishes workflow graph storage from CLR Activity implementation availability:

- A stored V1 graph could resume without registering the V1 WorkflowBase class in Host C.
- The Activity type named by that graph still had to be registered and implemented. With FinalizeVersion1Activity absent, Elsa substituted Elsa.NotFoundActivity; the continuation faulted and the original AutoBurn bookmark was consumed. The original graph could not be resumed by retrying the now-consumed bookmark.
- Therefore preserve old Activity implementations and their compatible registrations while active instances can reach them. Keeping only a V1 WorkflowBase class is not the specific requirement established by this test; retaining every Activity implementation referenced by stored graphs is.

## Same-version mutation behavior

A second process registered a changed graph using the exact same DefinitionId, WorkflowDefinition.Id, and Version as V1. The persisted StringData changed; the old instance then ran the changed V1-MUTATED finalizer. Same-version mutation is a demonstrated compatibility hazard, not an immutable-version guarantee. Treat a published version's code and graph as immutable and allocate a new version for behavior changes.

## Retraction behavior

Retraction is not deletion. In the tested case, retracting published V1 made it unavailable to new Published-selection starts, but did not remove its version row or invalidate its suspended instance/bookmark. The instance resumed successfully. This does not establish every trigger/provider behavior; only explicit selection, storage, and exact bookmark resume were tested.

## Deletion behavior

IWorkflowDefinitionManager.DeleteVersionAsync was called for V1 while it had a suspended instance. The operation succeeded and Elsa's deletion handler removed the V1 workflow instance and bookmark with the version. Do not delete a definition version while instances still depend on it in this configuration. The operation ran only against a generated disposable database.

## Bookmark/version compatibility

The old bookmark's ID, hash, ActivityInstanceId, and owning WorkflowInstanceId remained unchanged after V2 registration/publication. Resuming it completed the old instance using V1. This is direct evidence that publishing a different version did not rewrite or invalidate that persisted bookmark.

## SQL persistence evidence

The tests queried Elsa's ManagementElsaDbContext for WorkflowDefinitions and WorkflowInstances, and Elsa's IBookmarkStore for bookmark records. SQL rows exposed DefinitionId, Id, Version, IsLatest, IsPublished, and the instance's DefinitionVersionId. Tests use the Elsa SQL persistence modules from ELSA-09 and a unique disposable database per case.

## Migration behavior

The tested lifecycle did not automatically migrate a running instance when V2 was registered or published. Resume selected the graph by the instance's stored DefinitionVersionId. Elsa 3.8.4 does provide an explicit Elsa.Alterations Migrate alteration: its tagged handler resolves the requested target version and calls SetWorkflowGraphAsync. Elsa's tagged integration test exercises Migrate from stored definition version 1 to 2. This experiment did not execute Migrate against these typed code-first V1/V2 definitions, so code-first migration compatibility and data transformation remain unverified. Migration should be a deliberate operation, not an assumed deployment side effect.

## Recommended EDMS deployment policy

Based on these tests:

1. Give each behavior change a new integer version and distinct version ID under the same logical DefinitionId.
2. Start new reviews through the Published selector; publish only after validation.
3. Keep running instances pinned unless an explicitly approved Elsa alteration is executed and tested.
4. Keep compatible old Activity implementations registered while active instances can execute them. The stored WorkflowBase graph alone does not replace the Activity code.
5. Never mutate a published DefinitionId/version in place. The test proved Elsa can overwrite that stored graph and change an old instance's continuation.
6. Retraction can remove a version from new published selection while preserving the active wait in the tested path. Treat it as a selection action, not cleanup.
7. Do not delete a version while active instances reference it. Native deletion removed the active instance and bookmark in this SQL-backed host.
8. Track active workflow instances by DefinitionVersionId before retiring old Activity code or deleting a stored version.
9. Keep workflow version, document revision, review cycle, and application release as separate domain values.

Applied to a deployment like Engineering Review V3 with 300 suspended revisions followed by V4 adding HSE review: tests support that old instances continue the V3 stored graph while new starts resolve the published V4, provided old Activity implementations remain available. This was proved with sample V1/V2 instances, not at production scale.

## Confirmed from Elsa 3.8.4 source

Source-confirmed claims are limited to the tagged implementation:

- WorkflowIdentity contains DefinitionId, Id, Version, and optional TenantId.
- WorkflowBuilder defaults Publication to LatestAndPublished. A LatestDraft publication flag combination was explicitly used in tests.
- WorkflowDefinition.DefinitionId is the shared logical key; WorkflowDefinition inherits IsLatest/IsPublished and Version; its Id is the version-row identity.
- WorkflowInstance stores DefinitionId, DefinitionVersionId, and Version.
- The code-first definition store populator matches existing definitions by DefinitionId and specific Version. On a matching version it updates the stored serialized workflow data; this is the same-version mutation mechanism observed in the test.
- The typed workflow materializer deserializes the stored serialized graph.
- LocalWorkflowClient resolves an existing instance graph with a handle based on its DefinitionVersionId.
- Publisher and manager expose distinct publish, retract, revert, and delete-version operations.
- Delete-version notifications invoke a handler that deletes instances filtered by the deleted version ID.
- The Migrate alteration handler explicitly resolves a target version under the same logical definition ID and sets it on the active workflow execution context.
- Elsa's tagged alteration integration test proves Migrate for stored JSON definitions. This is not executable proof for the lab's typed code-first graphs.

## Limitations

- Version runtime tests use Elsa 3.8.4, .NET 10, and SQL Server 2022. The process test covers a graceful process exit and fresh process startup; no process-kill fault at arbitrary write boundaries was needed for this versioning question.
- The experiment uses fixed sample document data in V1/V2 definitions. It tests version selection and continuation, not workflow input binding.
- Versions and Activity type names are intentionally small examples. No 300-instance load, concurrent publication, multi-tenant versioning, trigger selection, or clustered cache invalidation was tested.
- The missing-Activity test demonstrates a destructive failure path for an auto-burning bookmark. It does not test every possible missing service/Activity dependency.
- Explicit Migrate was source-checked and found in Elsa's tagged integration test, but not run against these code-first definitions. No automatic migration was observed or established.
- Retraction/deletion findings apply to the tested SQL Server management/runtime configuration. In particular, native deletion did remove an active instance and bookmark in this setup.

## Executable evidence

- [Code-first V1/V2/mutation workflows and marker Activities](../../src/ElsaLab.Runner/Workflows/DocumentReviewDefinitionVersions.cs)
- [SQL version, publication, pinning, coexistence, retraction, deletion, mutation, old-Activity availability tests](../../tests/ElsaLab.Tests/WorkflowDefinitionVersioningTests.cs)
- [Two-process V1-to-V2 deployment harness](../../tests/ElsaLab.ProcessHarness/WorkflowVersioningProcessHarness.cs)
- [OS process-boundary assertion](../../tests/ElsaLab.Tests/ElsaProcessRestartTests.cs), test V1SuspendedInProcessA_RemainsPinnedWhenProcessBDeploysAndStartsV2
- SQL integration tests require ELSALAB_SQLSERVER_CONNECTION_STRING.

## Elsa source references

- [WorkflowIdentity](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/WorkflowIdentity.cs)
- [WorkflowPublication](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/WorkflowPublication.cs)
- [IWorkflowBuilder](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowBuilder.cs) and [WorkflowBuilder](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Builders/WorkflowBuilder.cs)
- [WorkflowDefinition](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Entities/WorkflowDefinition.cs), [VersionedEntity](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Entities/VersionedEntity.cs), and [WorkflowInstance](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Entities/WorkflowInstance.cs)
- [DefaultWorkflowDefinitionStorePopulator](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/DefaultWorkflowDefinitionStorePopulator.cs)
- [WorkflowDefinitionPublisher](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Services/WorkflowDefinitionPublisher.cs) and [WorkflowDefinitionManager](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Services/WorkflowDefinitionManager.cs)
- [DeleteWorkflowInstances notification handler](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Handlers/Notifications/DeleteWorkflowInstances.cs)
- [LocalWorkflowClient](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/LocalWorkflowClient.cs)
- [TypedWorkflowMaterializer](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Materializers/TypedWorkflowMaterializer.cs), [ActivityJsonConverter](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Serialization/Converters/ActivityJsonConverter.cs), and [definition cache eviction handler](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Handlers/Notifications/EvictWorkflowDefinitionServiceCache.cs)
- [Migrate alteration type](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Alterations/AlterationTypes/Migrate.cs), [Migrate handler](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Alterations/AlterationHandlers/MigrateHandler.cs), and [tagged migration integration test](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Alterations.IntegrationTests/MigrationTests.cs)
- [Workflow definition versioning integration tests](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/WorkflowDefinitionVersioning/Tests.cs)
