# Elsa Integration Boundary

Elsa is a deliberate orchestration dependency in the proposed EDMS. The integration should be narrow and visible, not a generic workflow-engine abstraction.

## What belongs in Workflow Integration

- Code-first WorkflowBase definitions and version identities.
- Thin custom Activities that bind inputs/outputs and call application services.
- Workflow input mapping from business IDs/configuration.
- Starting the intended published version.
- Persisting workflow/task linkage in EDMS: WorkflowInstanceId, DefinitionVersionId, BookmarkId, TaskId, CorrelationId.
- Application-authorized exact bookmark resume and cancellation requests.
- Idempotent start/resume dispatch, outbox consumption, reconciliation, and version-retention checks.
- Elsa SQL management/runtime persistence configuration and deployment/migration runbooks.
- Integration tests that keep the actual Elsa runtime in use.

## What stays outside Elsa

Document validity, current revision, comment meaning, review assignment eligibility, user/project authorization, task ownership, transmittal issuance rules, file path/key safety, and business audit remain EDMS responsibilities. Workflow variables/output may cache small orchestration values, but they are not the only durable copy of these facts.

## Activity shape

    Flowchart node
        ↓ Elsa typed input/output binding
    thin Activity
        ↓ normal DI
    application service command
        ↓
    domain / infrastructure

The Activity may resolve normal .NET services from ActivityExecutionContext, pass the Elsa cancellation token, call a service, and set native Elsa outputs. Do not put per-run mutable fields on Activity instances. Do not use Activity code for System.IO, SQL statements, authorization policy, review consolidation, or operation de-duplication.

## Human task and bookmark path

The capstone uses three Elsa RunTask nodes. The dispatcher creates EDMS ReviewTask records from task request context and associates the task with its workflow/bookmark. A reviewer command follows:

    authenticated user
       ↓
    load task and business target
       ↓
    authorize project / assignment / claim / action
       ↓
    commit task result + integration operation/outbox
       ↓
    resume exact Elsa bookmark

The exact bookmark is a continuation handle, not an authorization credential. The EDMS Task record is authoritative for assignee/candidate, claim state, result, and audit. On the Elsa 3.8.4 tag, RunTask plus dispatcher/reporter contracts were found, but no full EDMS-like task-management subsystem was found in the inspected tagged source tree.

## Resume data and domain authority

ELSA-08 found that resume-time dictionary input did not flow through downstream GetInput<T> for the tested registered code-first IWorkflowResumer path. The capstone therefore stores the reviewer decision/comment in the EDMS task service before bookmark resume; consolidation reads completed task records. Do not rely on resume payload as the only copy of a business decision until a dedicated compatibility test proves the exact host path.

## Durable dispatch and idempotency

EDMS commit and Elsa start/resume are not assumed atomic. Use an outbox or integration operation:

    EDMS transaction:
      save result/task state
      save pending ResumeBookmark operation
    dispatcher:
      resume bookmark
      record delivered/result

The dispatcher may deliver more than once. Use stable operation identity and make consumer/service behavior idempotent. If a bookmark is already consumed, reconcile current workflow/task state rather than repeat a business effect blindly.

## Correlation and audit

Carry a CorrelationId through:

    ProjectId
    DocumentId
    DocumentRevisionId
    ReviewCycleId
    ReviewAssignmentId / TaskId
    WorkflowInstanceId
    DefinitionVersionId
    ActivityId / ActivityExecutionId
    BookmarkId
    OperationId

Use Elsa’s runtime state, execution records, bookmarks, timers, and incidents for technical troubleshooting within their retention model. Keep a separate immutable EDMS AuditEvent for who changed a business record, why, and what changed. An Elsa execution journal is not by itself legal/business audit.

## Cancellation and timers

The capstone used Elsa’s native workflow client cancellation and separately cancelled EDMS task records. The application must coordinate these stores and verify that stale task commands and timers cannot trigger business actions after withdrawal. Use Elsa timers for workflow-owned deadlines only after defining calendar/time-zone semantics and operation idempotency. The existing tests are single-runtime and do not prove clustered timer ownership.

## Version deployment

New application starts select the approved published workflow version. Existing workflow instances are pinned in the tested path; deploy compatible Activity code for all live versions. Do not mutate a version in place or delete it with active instances. Keep migration explicit and controlled. See [deployment policy](DEPLOYMENT-POLICY.md) and [ELSA-13](../experiments/ELSA-13-workflow-versioning.md).

## Evidence and limits

- VERIFIED BY EXECUTABLE TEST: thin Activities/services, exact bookmark resume, SQL-backed task records and process restart, WaitAll, cancellation case, retry idempotency, and capstone version combination.
- CONFIRMED FROM ELSA 3.8.4 SOURCE: RunTask creates its task/bookmark integration point and dispatch/report contracts exist in the tag.
- ARCHITECTURAL INFERENCE: use an outbox, explicit authorization adapter, task domain, and technical/business audit split.
- NOT ESTABLISHED: atomic cross-database orchestration, multi-node exact-once resume, complete task service, full execution-history product UI.
