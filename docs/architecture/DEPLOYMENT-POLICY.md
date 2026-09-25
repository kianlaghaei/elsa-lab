# Proposed Deployment and Operations Policy

This policy is a product starting point derived from the Elsa 3.8.4 Lab. It must be validated with the selected hosting and operations environment.

## Runtime topology

Start with one ASP.NET Core Modular Monolith deployment containing the API/application layer and one active Elsa runtime, SQL Server persistence, and controlled file/object storage. Scale the API independently only if it does not create multiple competing Elsa schedulers. Before activating multiple workflow runtimes, test timer ownership, concurrent resume, duplicate dispatch, database contention, and side-effect idempotency.

Verified scope: ELSA-09/10/11 and the capstone used SQL Server and one active Elsa runtime at a time. They do not establish distributed scheduling or multi-node recovery.

## Database boundary

Recommended initial arrangement:

- EDMS database: documents, revisions, review cycles, tasks, comments, files metadata, transmittals, audit, outbox, integration operations.
- Elsa database(s): Elsa management definitions/instances and runtime bookmarks/execution state.
- Same SQL Server instance may host both for operational simplicity, with separate databases, credentials/permissions, backups, migration ownership, and monitoring.

Separate databases make ownership and restore boundaries explicit. A one-database/two-schema layout is a valid later operations choice, but does not make Elsa and EDMS writes one ACID transaction. Cross-boundary start/resume should use durable integration operations/outbox and reconciliation.

## Workflow definition lifecycle

1. Assign each logical workflow a stable DefinitionId.
2. Give every behavior change a new immutable definition-version identity and version number.
3. Test the exact definition before selecting it for new work.
4. Publish the version through the intended deployment process; new instances select the chosen latest published version.
5. Keep old workflow and Activity code available while persisted instances can still execute that version.
6. Track active-instance counts/version references before retirement.
7. Retract only to prevent normal new selection; do not assume it migrates or deletes running work.
8. Never delete a version referenced by active instances. In the tested Elsa 3.8.4 SQL path, deletion removed the associated live instance and bookmark.
9. Same logical ID + same version identity is not an immutability guarantee; same-version code mutation replaced stored graph behavior in the tested code-first path.
10. Do not automatically migrate live instances. Elsa has an explicit Migrate alteration in tagged source, but typed code-first migration was not verified in this Lab.

## Activity compatibility and deployment

Treat Activity types and serialized inputs/outputs as versioned runtime dependencies. Additive workflow changes should be tested against old persisted state. If an old Activity implementation must change, preserve a compatible implementation or intentionally execute a tested migration/retirement plan. Do not remove old Activity code just because new workflow definitions no longer reference it.

## SQL migrations and startup

ELSA-09..13 used Elsa’s provider configuration and automatic schema migration in isolated test databases. This is test/lab evidence only. Production should use reviewed, versioned migrations or a controlled deployment migration step, backups, maintenance windows where needed, and a documented rollback/forward-fix plan. Do not let every application replica race to migrate production schema at startup.

EDMS migrations are owned by EDMS; Elsa provider migrations are owned by the Elsa deployment version. Coordinate the release but preserve separate ownership.

## Files and backups

Store canonical revision assets under stable immutable keys with hashes and metadata. Back up file/object storage consistently with EDMS file references. A database restore without the referenced files (or file restore without matching metadata) is incomplete. Define retention, replication, legal hold, encryption, malware scanning, and recovery-point objectives before production.

## Recovery and idempotency

ELSA-10 proves recovery of a workflow already committed as Suspended after a process exits or is killed. It does not prove recovery when a process dies during an Activity side effect. ELSA-12 and EDMS-FIT-01 show the application must make retried side effects idempotent. Persist operation IDs, command fingerprints, and outcomes; add reconciliation for uncertain “applied then lost acknowledgement” cases.

## Upgrade procedure

Pin Elsa packages. For each proposed upgrade:

1. Read the exact tagged source/release notes for changed APIs and persistence behavior.
2. Update ElsaLab in an isolated branch and run restore/build/test.
3. Run SQL Server, process-restart, timer, resilience, versioning, and EDMS capstone categories against the target database/provider.
4. Verify bookmark payload, definitions, instances, activity identities, incidents, timers, and resumed outputs survive the provider transition.
5. Review old workflow/Activity compatibility and schema migration/restore procedure.
6. Update version notes and production deployment plan only after evidence passes.

The Lab’s findings are verified only against Elsa 3.8.4; forward compatibility is not assumed.

## Business and technical audit

Maintain EDMS AuditEvent separately from Elsa technical state. Correlate through WorkflowInstanceId, DefinitionVersionId, ActivityId/ActivityExecutionId, BookmarkId, TaskId, DocumentRevisionId, ReviewCycleId, CorrelationId, and OperationId. Define retention and access control for both.

## Evidence categories

- VERIFIED BY EXECUTABLE TEST: SQL persistence, committed suspended-state process restart, task row/workflow continuation, version pinning, timers, and idempotent retry within tested single-node scope.
- ARCHITECTURAL INFERENCE: database separation, single active runtime, deployment gates, migration controls, and recovery/reconciliation runbooks.
- NOT ESTABLISHED: cluster failover, active-Activity crash recovery, capacity, production backup/restore, and all upgrade paths.
