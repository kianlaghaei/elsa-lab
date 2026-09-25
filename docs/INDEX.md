# ElsaLab Documentation Index

This is the entry point for developers and coding agents. The knowledge base is tied to executable experiments and a named Elsa version; use the experiment and evidence links before applying a finding elsewhere.

## Start Here

- [README](../README.md) — purpose, current progress, and how to run the lab.
- [ROADMAP](../ROADMAP.md) — experiment status; later capabilities remain untested until their own work item.
- [LEARNINGS](LEARNINGS.md) — concise verified findings and caveats.
- [Elsa 3.8.4 version baseline](versions/elsa-3.8.4.md) — package, target framework, and upgrade rule.

## Experiments

Each experiment page records observed behavior, source-confirmed details, inference, limitations, executable evidence, and version-pinned Elsa references.

- [ELSA-01 — Basic Code-First Execution](experiments/ELSA-01-basic-execution.md)
- [ELSA-02 — Workflow Data](experiments/ELSA-02-data-model.md)
- [ELSA-03 — Custom Activity + Dependency Injection](experiments/ELSA-03-custom-activity-di.md)
- [ELSA-04 — Flowchart + Conditional Routing](experiments/ELSA-04-flowchart-routing.md)
- [ELSA-05 — Loops and Revision Cycles](experiments/ELSA-05-revision-cycles.md)
- [ELSA-06 — Parallel Execution + Join](experiments/ELSA-06-parallel-join.md)
- [ELSA-07 — Blocking Activity / Bookmark](experiments/ELSA-07-blocking-bookmark.md)
- [ELSA-08 — External Event + Suspend / Resume](experiments/ELSA-08-external-resume.md)
- [ELSA-09 — SQL Server Persistence](experiments/ELSA-09-sql-server-persistence.md)
- [ELSA-10 — Process Kill + Restart + Resume](experiments/ELSA-10-process-restart-recovery.md)
- [ELSA-11 — Timers / Delay / SLA / Escalation](experiments/ELSA-11-sla-timers-escalation.md)
- [ELSA-12 — Failure Handling + Retry](experiments/ELSA-12-failure-retry-incidents.md)
- [ELSA-13 — Workflow Definition Versioning](experiments/ELSA-13-workflow-versioning.md)

## EDMS Fit Tests

These separate fit tests evaluate whether already-tested Elsa patterns fit future EDMS application boundaries. They are not numbered Elsa roadmap milestones.

- [EDMS-FIT-01 — Document Storage and Workflow Side Effects](fit-tests/EDMS-FIT-01-document-storage.md) — physical-move adapter, service-owned file semantics, metadata, and idempotency.
- [EDMS-CAPSTONE-01 — End-to-End EDMS Architecture Fit](fit-tests/EDMS-CAPSTONE-01.md) — multi-discipline task/bookmark flow, comments and revisions, restart, SLA, idempotency, version coexistence, and concurrency smoke evidence.

## Architecture

- [Architecture gate decision](decisions/EDMS-ARCHITECTURE-GATE.md) — GO WITH CONDITIONS decision, evidence, remaining roadmap disposition, risks, and EDMS-00 recommendation.
- [Proposed EDMS architecture](architecture/PROPOSED-EDMS-ARCHITECTURE.md)
- [Domain model blueprint](architecture/DOMAIN-MODEL.md)
- [Modular monolith boundaries](architecture/MODULE-BOUNDARIES.md)
- [Elsa integration boundary](architecture/WORKFLOW-INTEGRATION.md)
- [Deployment policy](architecture/DEPLOYMENT-POLICY.md)
- [Open business questions](architecture/OPEN-QUESTIONS.md)

## Reference

These pages synthesize repeated knowledge and point back to experiments for evidence.

- [Workflow data](reference/workflow-data.md)
- [Activities and dependency injection](reference/activities-and-di.md)
- [Flowchart routing](reference/flowchart-routing.md)
- [Flowchart cycles](reference/flowchart-cycles.md)
- [Parallel execution and join](reference/parallel-and-join.md)
- [Blocking Activities and Bookmarks](reference/blocking-and-bookmarks.md)
- [SQL Server persistence](reference/sql-server-persistence.md)
- [Process restart recovery](reference/restart-recovery.md)
- [Timers and SLA](reference/timers-and-sla.md)
- [Failure, retry and incidents](reference/failure-retry-incidents.md)
- [Workflow definition versioning](reference/workflow-versioning.md)

## Patterns

- [Thin Activity calling an application service](patterns/activity-application-service.md) — based on ELSA-03.

## Version Notes

- [Elsa 3.8.4 baseline](versions/elsa-3.8.4.md)
- All thirteen completed experiments were verified against Elsa 3.8.4 and .NET 10 (`net10.0`). Re-run relevant experiments before relying on these findings after an Elsa upgrade.

## Current Knowledge Coverage

| Concept | Verified in |
|---|---|
| Code-first workflow execution | ELSA-01 |
| EDMS task records linked to RunTask bookmarks; candidate, claim, unclaim, completion, and authorization adapter | EDMS-CAPSTONE-01 (test adapter only) |
| Elsa 3.8.4 full User Task assignment/claim backend | Not found in inspected tagged source; re-evaluate on upgrade |
| Three-discipline WaitAll with external task completion and SQL task records | EDMS-CAPSTONE-01 |
| Revision/comment/transmittal integration and real process restart during review | EDMS-CAPSTONE-01 (revision repository remains in-memory in restart harness) |
| Early review completion before SLA escalation and application cancellation | EDMS-CAPSTONE-01; narrow single-runtime scenarios |
| 100-workflow / 300-task concurrency correctness smoke | EDMS-CAPSTONE-01 (not capacity certification) |
| Workflow inputs, variables, outputs, and typed result | ELSA-02 |
| Custom Activity, DI, Activity output, cancellation-token propagation, and tested Activity reuse | ELSA-03 |
| Conditional routing with `FlowDecision` | ELSA-04 |
| Loops and revision cycles | ELSA-05 |
| Parallel execution and join | ELSA-06 |
| Blocking Activity / bookmark creation and suspended in-memory state | ELSA-07 |
| Exact bookmark-ID resume through Elsa's in-process runtime | ELSA-08 |
| AutoBurn consumption, no-callback AutoComplete, duplicate exact resume, and instance isolation | ELSA-08 |
| SQL Server persistence for suspended instances and bookmarks; fresh-provider rehydration/resume | ELSA-09 |
| Process exit/kill after a committed suspension, fresh-process reload, and exact bookmark resume | ELSA-10 |
| Durable `Delay` bookmark in SQL and overdue timer restored after single-node process restart | ELSA-11 |
| Reminder/escalation sequence and early review completion cancelling the tested timer branch | ELSA-11 |
| Polly retry policy for a resilient Activity; transient vs permanent exception classification | ELSA-12 |
| Successful retry-attempt recording and SQL round trip; exhausted-attempt recorder boundary | ELSA-12 |
| FaultStrategy, ContinueWithIncidentsStrategy and persisted incident behavior | ELSA-12 |
| Side-effect retry safety with application idempotency and conflicting operation-key rejection | ELSA-12; EDMS-FIT-01 |
| Faulted workflow incident survives SQL reload and actual process exit | ELSA-12 |
| Manual alteration retry and recovery from an interrupted active Activity | Not verified |
| Clustered scheduling ownership and distributed timer delivery | Not verified |
| Interrupted active Activity recovery | Not verified |
| Distributed/multi-node recovery and concurrent resume ownership | Not verified |
| Stimulus-based bookmark matching / correlation | Not yet tested |
| Resume-time data consumed through workflow inputs | Not verified in the tested `IWorkflowResumer` code-first path (ELSA-08) |
| SQL persistence across fresh DI/runtime reconstruction | ELSA-09 |
| Timers, delay, SLA, and escalation | ELSA-11 |
| Failure handling and retry | ELSA-12 |
| Workflow definition identity, latest/published selection, pinned instances, and old-code compatibility | ELSA-13 |
| Same-version code-first mutation can overwrite a stored definition and alter suspended-instance continuation | ELSA-13 |
| Retraction preserves tested active instances; deleting a referenced definition version deletes its instance and bookmark | ELSA-13 |
| Explicit Migrate alteration exists; typed code-first migration not tested | Elsa 3.8.4 source and integration test; code-first behavior unverified |
| End-to-end workflow cancellation/interruption lifecycle | Not yet tested (ELSA-14); token propagation to a service was tested in ELSA-03 |
| Broad execution history/audit/observability | Not yet tested (ELSA-15); selected in-process journal fields were inspected in ELSA-02 through ELSA-04 |
| State machine evaluation | Not yet tested (ELSA-16) |
| Human Task backend | Not yet tested (ELSA-17) |
| Load and concurrency | Not yet tested (ELSA-18) |
| EDMS-like end-to-end workflow | Not yet tested (ELSA-19) |

EDMS-CAPSTONE-01 is a separate fit test and provides partial end-to-end evidence. It does not mark ELSA-14 through ELSA-19 DONE; see the [architecture gate](decisions/EDMS-ARCHITECTURE-GATE.md) for what remains needed, partially covered, recommended to skip, or deferred.

## Executable Evidence

- Current runner and code-first workflow: [`src/ElsaLab.Runner`](../src/ElsaLab.Runner/)
- Current automated tests: [`tests/ElsaLab.Tests`](../tests/ElsaLab.Tests/)
- ELSA-01 and ELSA-02 implementations were later replaced as the experiment progressed; their experiment pages link to the exact source and test files at their implementation commits.
