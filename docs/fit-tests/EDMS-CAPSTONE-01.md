# EDMS-CAPSTONE-01 — End-to-End EDMS Architecture Fit

Status: VERIFIED FOR THE LAB SCENARIOS  
Elsa: 3.8.4  
.NET: 10  
Database integration: SQL Server 2022 (major version 16)  
Verified date: 2026-09-25  
Implementation commit: d23c5c4ef23f86117c50d492cc1b48f320e5d17e

## Purpose and scope

This capstone combines previously verified Elsa capabilities with a small EDMS-shaped application boundary. It is evidence for platform suitability and architecture decisions. It is not a production EDMS, a load certification, a complete task system, or a durable implementation of every EDMS domain record.

The tested shape is:

    EDMS-owned records and services
              ↓
    thin Elsa Activities and native workflow constructs
              ↓
    Elsa runtime and SQL persistence

## Human Task evaluation

### Source-confirmed for the Elsa 3.8.4 tag

The 3.8.4 source contains the workflow-runtime RunTask Activity and its dispatch/reporting contracts. RunTask creates a resumable task bookmark and delegates task dispatch; TaskReporter can provide a result to the waiting workflow. The tagged tree inspected for this gate did not contain a separate User Tasks module that owns an EDMS-style task lifecycle with durable assignments, candidate pools, claim/unclaim, reassignment, delegation, substitution, task inbox queries, or business authorization.

Relevant tagged source:

- [RunTask.cs, Elsa 3.8.4](https://raw.githubusercontent.com/elsa-workflows/elsa-core/3.8.4/src/modules/Elsa.Workflows.Runtime/Activities/RunTask.cs)
- [ITaskDispatcher.cs, Elsa 3.8.4](https://raw.githubusercontent.com/elsa-workflows/elsa-core/3.8.4/src/modules/Elsa.Workflows.Runtime/Contracts/ITaskDispatcher.cs)
- [TaskReporter.cs, Elsa 3.8.4](https://raw.githubusercontent.com/elsa-workflows/elsa-core/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/TaskReporter.cs)
- [RunTaskStimulus.cs, Elsa 3.8.4](https://raw.githubusercontent.com/elsa-workflows/elsa-core/3.8.4/src/modules/Elsa.Workflows.Runtime/Stimuli/RunTaskStimulus.cs)

This is a version-scoped source finding, not a claim about later Elsa releases.

### Verified by executable test

The workflow creates three distinct Elsa RunTask waits, one per Process, Mechanical, and Instrument review. The registered ITaskDispatcher calls an EDMS application service, which persists one ReviewTask per Elsa wait in the application-owned task store. The test then exercises candidate checking, claim, unclaim/reclaim, completion, and exact bookmark resume. The SQL process test proves task rows and bookmarks survive Process A termination and are completed from a fresh Process B.

The application record links TaskId, WorkflowInstanceId, BookmarkId, ActivityId, ActivityExecutionId, CorrelationId, ProjectId, DocumentId, DocumentRevisionId, ReviewCycleId, ReviewAssignmentId, Discipline, candidates, status, claim/completion actors and timestamps, result, comment, and DefinitionVersionId. In production these are application fields with EDMS meaning; Elsa bookmark IDs are continuation locators.

### Decision

Use an EDMS Task/ReviewAssignment domain with Elsa bookmarks as continuation targets. Use the Elsa RunTask primitive where it suits the workflow’s wait/result semantics, but do not make Elsa the authority for assignees, candidate eligibility, claims, delegation, substitution, authorization, task inboxes, or business audit. The capstone task service is only a test adapter and is not a finished task application.

## Observed

### Scenario 1 — clean multi-discipline review

The V1 code-first Flowchart registers R2, records three logical Reference distributions, fans out to three named Elsa RunTask Activities, and waits at a FlowJoin configured as WaitAll. Tests prove three independently identified tasks and bookmarks were created. The reviewer simulation claims and completes all three tasks. Only after all three bookmark resumes does consolidation finish. The approved path physically moves the canonical test file from Incoming to Approved, applies the publication operation once, and creates one transmittal whose item snapshots the document revision and file hash. The final Elsa status/substatus are Finished/Finished, with no incidents, bookmarks, or faulted ActivityExecutionContexts.

The publication service is deliberately configured to apply the operation and then throw once. Elsa retries the Activity service call; the same operation identity yields AlreadyApplied on retry. The service reports two calls but one applied business operation.

### Scenario 2 — comments and new revision

Mechanical returns a comment on R2 while the other reviews approve it. Consolidation creates an EDMS comment tied to the exact R2 revision and review cycle; R2 ends RevisionRequired and is not published. R3 is then received as a separate revision under the same DocumentId and a new ReviewCycleId. The clean R3 cycle approves/publishes. The test observes LatestReceivedRevisionId and CurrentValidRevisionId both point to R3 after that approval, while the R2 comment remains present and scoped to R2/C1.

This is a minimal in-memory domain adapter, not a complete validity policy or comment-response system.

### Scenario 3 — restart during review

Process A starts V1 and reaches three SQL-persisted RunTask bookmarks. The parent process reads committed Elsa workflow state and application task rows, then terminates Process A while it is alive. Process B has a different OS PID, constructs a fresh host, loads the task rows/bookmarks, completes three tasks, and resumes the same workflow. The resulting workflow is Finished/Finished, has no bookmark, incident, or faulted Activity context, and reports RevisionRequired for the intentionally commented Mechanical review.

The SQL task store persists task rows and the workflow/runtime stores persist Elsa state. For this harness only, Process B reconstructs a minimal in-memory revision record from task fields. The revision, comments, distribution, and transmittal repositories themselves are not durable across that process boundary in this capstone. This is a major limitation; it must not be mistaken for a complete EDMS recovery test.

### Scenario 4 — SLA and withdrawal

For the early-completion race, the workflow starts with a RunTask wait and an Elsa Delay bookmark. Completing the review first produces OnTime, removes both waits, and no reminder/escalation action appears after waiting beyond the test deadline.

For withdrawal, the test calls Elsa’s workflow client cancellation API and separately marks outstanding EDMS tasks Cancelled. The observed workflow state is Finished/Cancelled; after the deadline there are no reminder actions and no remaining bookmarks.

These are focused in-process capstone checks. They do not prove a general cancellation protocol under every scheduler/process race.

### Scenario 5 — workflow version compatibility

The combined in-memory capstone test suspends a V1 instance, starts and completes a V2 instance with the added Coordinator check, then resumes V1. Outputs show V2/Coordinator true for the V2 instance and V1/Coordinator false for the old instance. ELSA-13, separately, supplies the SQL and real-process version-pinning evidence. This capstone test does not repeat the full SQL deployment matrix.

### Scenario 6 — distribution and concurrency

Service tests use a temporary directory to distinguish Reference, Copy, and Move distribution behavior and check repeat-safe operations. The main workflow selects Reference; the separate EDMS-FIT-01 established a real filesystem movement through a thin Elsa Activity.

The sanity test creates 100 workflow instances and 300 review tasks, then completes them with at most eight workflow-level workers. All 100 end without incidents or cross-instance identity leakage. The observed run took about 1.75 seconds in this environment. This is a correctness/concurrency smoke test, not a throughput benchmark, 50-user capacity claim, or SQL contention test.

## Elsa responsibility

VERIFIED BY EXECUTABLE TEST: Elsa owns the tested graph sequencing, fan-out/WaitAll, task wait bookmarks, timer wait, workflow outputs, workflow-version association in the separate ELSA-13 evidence, retry orchestration, SQL runtime state, and continuation after process termination once state is committed.

CONFIRMED FROM ELSA 3.8.4 SOURCE: RunTask supplies a workflow task/bookmark primitive and dispatch/report contracts. The tagged source inspected does not supply the complete EDMS task domain described above.

## EDMS responsibility

VERIFIED BY EXECUTABLE TEST: The application adapter owns review task identity/status/candidates/claim/result, comment records, revision identity, distributions, publication/storage operations, operation idempotency, and transmittal snapshots. Application-side authorization is checked before claim/completion/resume in the tested service path.

ARCHITECTURAL INFERENCE: Keep business truth in EDMS stores. A workflow output or execution log is useful technical state, but it must not become the only source for revisions, comments, assignments, file identity, or issued transmittals.

## Reliability and audit findings

- VERIFIED BY EXECUTABLE TEST: Elsa can retry a side-effecting Activity, but does not make arbitrary external effects exactly-once. The service operation identity made the apply-then-throw test safe.
- VERIFIED BY EXECUTABLE TEST: the capstone traced a task through correlation/document/revision/cycle/task IDs to its bookmark, Activity ID/execution ID, side-effect operation ID, and transmittal snapshot.
- NOT ESTABLISHED: the capstone did not export a complete durable per-Activity Elsa execution journal through the client state export. It checks WorkflowState, outputs, incidents, faulted contexts where exposed, task rows, and selected runtime identifiers.
- ARCHITECTURAL INFERENCE: retain an immutable EDMS AuditEvent stream for legally relevant business events. Elsa technical execution history answers runtime troubleshooting questions, not the full business-audit question.

## Production recommendation

Use stable immutable file/object keys for canonical revision assets. Keep document/revision status and logical location in EDMS metadata. Use Reference distribution by default when reviewers can access the canonical immutable revision. Use Copy when a workspace needs an independently retained snapshot. Use Move only where a contractual or storage-system requirement demands a physical move. Any operation that crosses a workflow/service/storage boundary needs a stable OperationId, command conflict validation, and reconciliation; filesystem/object storage plus database metadata is not one ACID transaction.

Start with code-first workflows and configuration-driven disciplines, candidate rules, and SLA values. Keep published workflow versions immutable and retain compatible old workflow/Activity code according to ELSA-13. Do not expose bookmark IDs as authorization tokens.

## Limitations

- The application domain repositories except the review-task table are in memory. Process B reconstructs a minimal revision row from a task record.
- The restart capstone exercises one SQL Server instance and one active Elsa runtime at a time. It is not a clustered/multi-node test.
- The 100×3 scenario is a correctness smoke test only.
- Reviewer actions are test calls into the application task service, not authenticated web/API requests.
- Assignment/candidate policy is a single test candidate per discipline. Delegation, substitution, team pools, working calendars, and complex authorization are not implemented.
- Distribution mode variations are service tests; the end-to-end workflow uses Reference distribution.
- SLA timing is short real time. The capstone’s early completion and withdrawal checks do not establish every cancellation race after process restart.
- Transmittal is a minimal immutable snapshot. Numbering, recipients, delivery, signatures, and legal retention are not defined.
- No production SQL schema, outbox, notification provider, or full business audit is implemented.

## Executable evidence

- [Capstone workflow definitions](../../src/ElsaLab.Runner/Workflows/EdmsCapstoneWorkflows.cs)
- [Thin capstone Activities](../../src/ElsaLab.Runner/Activities/EdmsCapstoneActivities.cs)
- [EDMS task application service and Elsa dispatcher](../../src/ElsaLab.Runner/Capstone/EdmsReviewTaskApplicationService.cs)
- [SQL application-task store](../../src/ElsaLab.Runner/Capstone/SqlEdmsReviewTaskStore.cs)
- [Minimal capstone service and domain adapter](../../src/ElsaLab.Runner/Capstone/InMemoryEdmsCapstoneService.cs)
- [In-process scenarios](../../tests/ElsaLab.Tests/EdmsCapstoneTests.cs)
- [SQL/process restart scenario](../../tests/ElsaLab.Tests/EdmsCapstoneProcessTests.cs)
- [Separate-process harness](../../tests/ElsaLab.ProcessHarness/EdmsCapstoneProcessHarness.cs)

## Verification result

- VERIFIED BY EXECUTABLE TEST: dotnet restore succeeded.
- VERIFIED BY EXECUTABLE TEST: dotnet build succeeded with 0 warnings and 0 errors.
- VERIFIED BY EXECUTABLE TEST: full dotnet test passed 58 tests, 0 failed, 0 skipped, with the SQL Server test connection configured.
- VERIFIED BY EXECUTABLE TEST: explicit Category=SqlServer|Category=ProcessRestart|Category=EdmsCapstone filter passed 29 tests, 0 failed, 0 skipped.
- VERIFIED BY EXECUTABLE TEST: the in-memory capstone group passed 7 tests; the dedicated capstone process test passed 1 test.

The SQL test suite used a local SQL Server 2022 instance and Windows integrated authentication. The environment connection was supplied only to the test process and was not written to the repository.

## Evidence classification

The documents linked from [the architecture gate](../decisions/EDMS-ARCHITECTURE-GATE.md) label material conclusions as VERIFIED BY EXECUTABLE TEST, CONFIRMED FROM ELSA 3.8.4 SOURCE, ARCHITECTURAL INFERENCE, BUSINESS DECISION REQUIRED, or NOT ESTABLISHED. The label applies only to the stated scope.
