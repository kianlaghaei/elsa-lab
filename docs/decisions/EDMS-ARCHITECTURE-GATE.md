# EDMS Architecture Gate

Decision date: 2026-09-25  
Evidence baseline: Elsa 3.8.4, .NET 10, SQL Server 2022 test instance  
Capstone implementation commit: d23c5c4ef23f86117c50d492cc1b48f320e5d17e

## Decision

### 1. Elsa suitability — GO WITH CONDITIONS

The Lab demonstrates that Elsa 3.8.4 can coordinate the workflow primitives needed for the proposed EDMS: code-first execution, conditional Flowcharts, finite revision cycles, fan-out/WaitAll, blocking bookmarks, exact external resume, SQL persistence, a committed suspended wait across process termination, durable timers, retry orchestration, and pinned workflow versions. EDMS-CAPSTONE-01 combines discipline tasks, comments, a new revision, publication/transmittal, SLA, cancellation, version coexistence, and an abrupt restart during a real suspended review.

This is not a claim that Elsa alone is the EDMS platform. The capstone’s review-task table is SQL-backed, but most domain data remains in a minimal in-memory adapter. The concurrency scenario is a smoke test, not capacity certification. Distributed hosting, cross-database consistency, business audit, complete authorization, and production operations remain to be designed.

Conditions:

- EDMS owns domain state, tasks, authorization, file semantics, business audit, idempotency, and reconciliation.
- Elsa persistence and EDMS persistence have separate responsibilities and explicit backup/migration practices.
- All external side effects are idempotent or protected by durable operation identity.
- Published workflow versions are immutable; old compatible workflow/Activity code remains available as long as active instances require it.
- Start single-node. Test multi-node scheduling/resume semantics before deploying multiple active runtimes.
- Complete business decisions listed in OPEN-QUESTIONS.md before freezing schema and MVP.

Classification: VERIFIED BY EXECUTABLE TEST for listed Elsa capabilities; ARCHITECTURAL INFERENCE for product topology and conditions.

### 2. Human Task decision — EDMS Task Domain + Elsa Bookmark

Elsa 3.8.4 tagged source has RunTask, task dispatch and task reporting integration points. The tested capstone used these to create workflow bookmarks, then persisted task assignment/claim/completion in an EDMS-owned SQL table and resumed exact bookmarks after authorization checks. The tag inspection did not find a full user-task management module for the required EDMS lifecycle.

Use EDMS-owned Task/ReviewAssignment records as the authoritative task backend. Elsa RunTask may remain the Activity-level waiting/result mechanism; exact bookmark identity is a continuation target only. Do not expose unrestricted bookmark resume.

Classification: CONFIRMED FROM ELSA 3.8.4 SOURCE for the primitive; VERIFIED BY EXECUTABLE TEST for task persistence/resume; ARCHITECTURAL INFERENCE for the chosen ownership boundary.

### 3. Workflow authoring — Code-first

Use code-first graphs for the initial product, with data/configuration for review requirements, assignments, and SLA durations. Elsa 3.8.4 version tests show that same-version mutation can replace stored graph behavior, and a missing Activity implementation can break a suspended instance. Code review, immutable versions, explicit deployment, and executable regression tests offer a controlled first release. Do not add Designer/Studio before product governance and customer customization needs are known.

Classification: VERIFIED BY EXECUTABLE TEST for version behavior; ARCHITECTURAL INFERENCE for authoring choice; BUSINESS DECISION REQUIRED for future customer-configurable workflow needs.

### 3a. Audit and runtime observability boundary

| Elsa evidence available in the Lab | Scope |
|---|---|
| Workflow instance ID, status/substatus, definition identity/version, workflow inputs/outputs/state | ELSA-02, ELSA-09, ELSA-13; persisted instance and version association were queried through Elsa stores. |
| Activity IDs, ActivityExecutionContext IDs/status, selected journal/state fields | ELSA-02 through ELSA-06 and capstone task linkage; some export paths expose only the root execution context. Do not assume every nested execution record is surfaced by every client API. |
| Bookmark ID, hash/name, owner workflow, Activity instance identity, payload | ELSA-07 through ELSA-10; persisted payload values survived SQL but deserialized to ExpandoObject in the tested path. |
| Delay/timer bookmark and due-time scheduling state | ELSA-11; process exit/overdue startup was tested in single-node mode. |
| Successful Activity retry-attempt records and workflow incidents | ELSA-12; successful retry attempt history persisted, while the exhausted-attempt path did not yield a normal recorder history. Persisted exception type was generalized by the tested provider path. |

VERIFIED BY EXECUTABLE TEST: the Lab can correlate selected task, bookmark, workflow, definition version, Activity execution, operation, and document/revision identifiers. NOT ESTABLISHED: a complete searchable execution journal, indefinite retention, or product-ready audit interface.

ARCHITECTURAL INFERENCE: maintain an immutable EDMS AuditEvent for business actions and use Elsa runtime data for technical execution diagnosis. Never treat Elsa execution history as the sole legal/business audit.

### 4. Hosting — Modular Monolith

Recommend ASP.NET Core Modular Monolith + Elsa Runtime + SQL Server + controlled file/object storage. Keep one active Elsa runtime initially. Separate API/use cases, EDMS modules, file adapters, and workflow definitions/Activities within one deployable product. Do not start with microservices. A separate worker can be introduced if timer/throughput/operational evidence later justifies it.

Classification: ARCHITECTURAL INFERENCE informed by single-process, SQL, and single-node tests. Multi-node runtime behavior is NOT ESTABLISHED.

### 5. Workflow deployment/version policy

Use ELSA-13’s observed rules:

1. A logical DefinitionId identifies the workflow family; definition version ID and integer Version identify a version record.
2. Do not mutate a published workflow version in place. Same-version code registration replaced stored graph content in the tested code-first path.
3. Publish a new version for changed behavior. New starts select the intended published version using the tested selector.
4. Existing instances remained associated with their V1 definition-version identity through V2 publication and restart.
5. Retain compatible Activity implementations needed by running instances.
6. Retraction prevented the tested version from normal published selection while preserving the tested suspended instance/bookmark.
7. Deleting a version with a live instance deleted that instance and bookmark in the tested SQL configuration. Prohibit deletion while instances depend on a version.
8. Elsa has an explicit Migrate alteration in the tagged source, but typed code-first migration was not established by this Lab. Do not migrate implicitly.

Classification: VERIFIED BY EXECUTABLE TEST except where explicitly noted source-only or untested; see [ELSA-13](../experiments/ELSA-13-workflow-versioning.md).

### 6. Reliability policy

- Classify transient infrastructure errors separately from permanent business errors; configure Elsa resilience deliberately.
- Retry is at-least-once invocation, not exactly-once external behavior.
- Reuse a stable operation key for a logical side effect; reject conflicting reuse.
- Record business-visible failures in EDMS incidents/audit as well as Elsa technical incidents where appropriate.
- A committed suspended workflow/timer survived process exit and resumed from SQL. An Activity interrupted mid-side-effect and distributed competing resume were not established.
- Timer completion/cancellation worked in the tested single runtime. Clustered timer ownership is not established.

Classification: VERIFIED BY EXECUTABLE TEST for ELSA-11/12 and EDMS-FIT-01; broader production procedures are ARCHITECTURAL INFERENCE.

### 7. Product implementation readiness

Yes—the uncertainty about whether Elsa can supply the required orchestration primitives is low enough to begin implementing the product foundation. Do not begin by implementing the complete workflow. Begin EDMS-00 — Solution Foundation with project/module structure and the document/revision/file vertical slice. The business decisions in OPEN-QUESTIONS.md remain necessary before final database design and MVP scope freeze.

## Capstone scenario results

| Scenario | Evidence and scope |
|---|---|
| Three-discipline clean approval | VERIFIED BY EXECUTABLE TEST: three distinct app tasks and Elsa bookmarks; reviewers claim/complete; WaitAll; storage publication; one idempotent transmittal; Finished/Finished. |
| Commented R2 then clean R3 | VERIFIED BY EXECUTABLE TEST: R2 comment remains tied to R2/cycle; R3 uses same DocumentId, a new revision and review cycle; latest received/current valid move to R3 after approval. Domain adapter is in-memory. |
| Restart during review | VERIFIED BY EXECUTABLE TEST: real Process A is killed after SQL state is committed; fresh Process B completes three app tasks and exact bookmarks; one comment routes revision to required. Task data and Elsa state are SQL-backed; revision repository was reconstructed in Process B. |
| SLA before escalation | VERIFIED BY EXECUTABLE TEST: task completion before Delay produces OnTime and no later reminder/escalation in the tested process. |
| Cancellation/withdrawal | VERIFIED BY EXECUTABLE TEST: workflow client CancelAsync yielded Finished/Cancelled; app tasks were separately marked Cancelled; no post-deadline reminder ran in this test. |
| Apply-then-throw retry | VERIFIED BY EXECUTABLE TEST: publication Activity service called twice, one operation applied under the same operation ID. |
| V1/V2 deployment | VERIFIED BY EXECUTABLE TEST in capstone in-memory combination; SQL/process pinning is independently established by ELSA-13. Old V1 resumed with V1 output; new V2 included Coordinator check. |
| Concurrency sanity | VERIFIED BY EXECUTABLE TEST: 100 workflows/300 tasks, eight workflow-level workers, no cross-instance state or fault. Approx. 1.75s observed locally; not a capacity benchmark. |

## Standalone roadmap disposition

The capstone is integration evidence and does not silently close roadmap experiments.

| Experiment | Disposition after gate |
|---|---|
| ELSA-14 Cancellation and Interruption | Partially covered by capstone’s single in-process withdrawal case. Standalone cancellation/interruption semantics remain needed. |
| ELSA-15 Execution History / Audit / Observability | Partially covered by identifiers and runtime inspection. Broad durable history and diagnostics remain needed; EDMS business audit is a separate design. |
| ELSA-16 State Machine Evaluation | Recommend skip for the initial EDMS. Keep TODO until a concrete state-centric use case demonstrates value beyond Flowchart. |
| ELSA-17 Human Task Backend Evaluation | Partially covered through tagged RunTask/source review and EDMS task adapter. The full 3.8.4 tag lacks the task-management subsystem sought; keep TODO as a scoped evaluation/upgrade recheck, not a production dependency. |
| ELSA-18 Load / Concurrency | Partially covered by 100×3 smoke test. Capacity, SQL contention, sustained load, and 50-user sizing remain unverified. |
| ELSA-19 EDMS-like End-to-End Workflow | Partially covered by this capstone. Do not mark DONE; this capstone is a test adapter, not a production-like vertical slice. |

Roadmap statuses remain unchanged.

## State Machine decision

Recommend skipping ELSA-16 for the first product release unless a concrete EDMS process needs state-centric transitions/guards that are materially clearer than the already tested Flowchart. The known review flows are sequences, branches, joins, revision cycles, bookmarks, timers, and explicit outcomes—all represented by tested Flowcharts/activities. This is an ARCHITECTURAL INFERENCE, not a finding that State Machine has no value.

## Database boundary

Keep EDMS domain tables and Elsa management/runtime persistence conceptually separate. Initial deployment may place two databases on one SQL Server instance; use separate ownership, migration, permission, restore, and monitoring procedures. Sharing an instance does not create a cross-database ACID transaction. If operations later require one database with separate schemas, preserve the logical ownership boundary and explicit non-atomic workflow dispatch.

## File architecture

Use a file-storage port backed by immutable revision assets with content hash, size, media type, and stable storage key. Store access/control and logical location in EDMS. Reference is the default distribution; Copy creates an isolated snapshot; Move is exceptional and must be idempotent. The capstone tests logical modes, while EDMS-FIT-01 tests real filesystem movement.

## Authorization boundary

Before a workflow resume, resolve the authenticated principal, project membership, role/team/discipline, assignment candidate or current owner, document revision state, and permitted action. A conceptual check is CanCompleteReview(user, project, revision, assignment). Only after authorization and durable EDMS task-result recording should the application resume the associated bookmark. BookmarkId is not a credential.

## Events and outbox

RevisionReceived, ReviewAssigned, ReviewCompleted, CommentCreated, RevisionApproved, and TransmittalIssued are candidates for domain events. An outbox is justified when committing EDMS state must reliably trigger an Elsa start/resume or external notification. The outbox record should include idempotent correlation/operation keys, and dispatch should be retry-safe. The capstone does not implement an outbox.

## Remaining risks

| Rating | Risk / action |
|---|---|
| HIGH | Multi-node ownership/concurrent resume and clustered scheduling are not established; remain single runtime until tested. |
| HIGH | EDMS/Elsa cross-database atomicity, transactional outbox, reconciliation, and durable business side-effect journal need design. |
| HIGH | Business calendars, revision codes, task ownership/delegation, and contractual review meaning need decisions before schema/MVP freeze. |
| HIGH | Capacity beyond the 100-instance correctness smoke test is unknown; define production workload and run representative SQL load tests before launch. |
| HIGH | Workflow/Activity version retention needs an operational deployment and deletion guard. |
| MEDIUM | Full cancellation/interrupted-Activity lifecycle and stale side-effect handling remain for ELSA-14 and follow-up integration work. |
| MEDIUM | Elsa execution records do not replace immutable EDMS business audit; define a joined support view and retention strategy. |
| MEDIUM | SQL migrations, backup/restore, upgrade/rollback, and recovery runbooks need operational validation. |
| LOW | Customer-editable graph design can be revisited after MVP governance and support demands are known. |

No BLOCKER is identified for starting EDMS-00. These open items constrain production readiness, not the ability to create a foundation slice.

## Elsa upgrade policy

Pin the tested package set. Before any Elsa upgrade, update ElsaLab in an isolated branch, run restore/build/all tests plus SQL, process, capstone, versioning, failure and timer categories, inspect tagged source changes in sensitive areas, update version notes, and only then consider the product dependency update. The current findings do not imply forward compatibility.

## Business questions

See [OPEN-QUESTIONS.md](../architecture/OPEN-QUESTIONS.md). Unresolved business semantics include revision numbering, approval/status definitions, workday calendars, transmittal contracts/numbers, comment carry-forward, retention/legal audit, external exchange, and bulk behavior. These cannot be answered by Elsa experiments.

## Next implementation milestone

EDMS-00 — Solution Foundation should create only the approved Modular Monolith project/module scaffold, architecture tests, SQL boundaries, file-storage port, and first vertical slice: Create Project → Register Document → Receive Revision → Store immutable file → Display Document/Revision. Do not build the full review workflow in EDMS-00.
