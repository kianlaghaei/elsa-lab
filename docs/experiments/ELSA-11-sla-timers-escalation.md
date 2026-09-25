# ELSA-11 — Timers, Delay, SLA and Escalation

Experiment: ELSA-11  
Status: VERIFIED  
Elsa: 3.8.4  
.NET: 10 (`net10.0`)  
Database: SQL Server 2022  
Verified date: 2026-09-25  
Implementation commit: [3d475607e78ae3b1483ebac8bc9fccd6e5d9a869](https://github.com/kianlaghaei/elsa-lab/commit/3d475607e78ae3b1483ebac8bc9fccd6e5d9a869)

## Observed

- The tested workflow uses Elsa's `Elsa.Scheduling` 3.8.4 package, `UseScheduling`, and the native `Delay` Activity. No external scheduler package was needed for this single-active-runtime scenario.
- Before a Delay is due, the SQL-backed workflow instance is `Running` / `Suspended`; the downstream reminder has not executed, and a runtime bookmark named `Elsa.Delay` is present. Its persisted `DelayPayload.ResumeAt` is in the future.
- When a timer expires, the bookmark is consumed and the sequence continues. The normal SLA path ran one reminder followed by one escalation, produced `ReminderCount = 1`, `EscalationCount = 1`, `Escalated = true`, and `FinalSlaStatus = "Escalated"`, and ended `Finished` / `Finished` without incidents or faulted Activity contexts.
- Runtime execution logs for `InitialSlaDelay` recorded `Started`, `Suspended`, `Resumed`, and `Completed` under the same `ActivityInstanceId`. The test observed the Activity execution context as `Running` while suspended. `SendReviewReminder` completed before `EscalateReview` started. Both action Activities reported the same Sequence parent ID; the log does not expose a separate temporal scheduling-predecessor field.
- Completing review first resumed Elsa's `Event` bookmark inside the `While` / `Fork` race. The workflow finished `Finished` / `Finished` with `FinalSlaStatus = "OnTime"`, zero reminders, no escalation action, and no remaining bookmarks. The test kept the runtime alive 2.5 seconds beyond the original due time and observed no stale timer action.
- In the two-instance test, the 1-second SLA finished while the 4-second SLA remained `Running` / `Suspended` with its own bookmark and no outputs. Both later completed with their own review keys and one reminder/escalation each.
- The in-memory review action service returned `Applied` and then `AlreadyApplied` for the same operation command, retained one action, and rejected reuse of an operation ID with a different command. This proves the service's local idempotency behavior only; the adapter's dictionary is not durable.
- SQL evidence came from Elsa's Management and Runtime EF Core contexts: a workflow instance row and a `RuntimeElsaDbContext.Bookmarks` row existed while the Delay was pending. After timer completion, the management record was `Finished` / `Finished` and the workflow had no bookmark.

## Timer/Delay model

The workflow uses `Elsa.Scheduling.Activities.Delay` with a workflow expression returning a `TimeSpan`. The scheduling module creates the timer bookmark and schedules its continuation. The runner host configures `UseScheduling(_ => { })` alongside SQL-backed workflow management and runtime persistence.

The tested launch path uses `IWorkflowRuntime.CreateClientAsync()` and `CreateAndRunInstanceAsync` after populating Elsa's definition store and resolving the registered code-first definition version. In this setup, starting the Delay workflow with the basic `IWorkflowRunner` persisted its bookmark but did not put it on the local scheduler. The runtime-client path did schedule it and was used for the timed experiments.

Because inputs supplied at launch are request data, not a durable continuation contract, the workflow copies document/review keys, delay values, counters, and status into Elsa variables configured with `.WithWorkflowStorage()` before the first Delay. The tests observed those values and final outputs after a timer continuation.

## SQL persistence

Both workflow management and runtime persistence use the SQL Server EF Core providers already configured by ELSA-09/10. The tests used a unique SQL Server database per test and the `ELSALAB_SQLSERVER_CONNECTION_STRING` environment variable; no connection string is stored in the repository.

The pending workflow instance was observed through `IWorkflowInstanceStore` and `ManagementElsaDbContext`. The timer bookmark was observed through `IBookmarkStore` and the `RuntimeElsaDbContext.Bookmarks` table. The stored bookmark included the owner workflow ID, bookmark ID/name, activity instance ID, and a serialized `ResumeAt` payload. On completion the bookmark was absent and the persisted instance held the finished workflow state and outputs.

## Normal timer firing

The main sequence is:

```text
AssignReview
  → InitialSlaDelay
  → SendReviewReminder
  → RecordReminder / mark ReminderSent
  → ReminderEscalationDelay
  → EscalateReview
  → RecordEscalation / mark Escalated
  → publish outputs / complete
```

Tests used short real delays (typically 1 second) and polled persisted workflow state with a bounded 20-second timeout. The timer fired once in the tested single-node execution. The runtime log sequence and service action records independently prove ordering and counts.

## Reminder and escalation

`SendReviewReminderActivity` and `EscalateReviewActivity` are thin Elsa `CodeActivity` adapters. They read typed Elsa inputs, resolve `IReviewSlaActionService` through `ActivityExecutionContext`, pass `ActivityExecutionContext.CancellationToken`, and set an Elsa output port with the service result. Business action behavior stays in the normal service.

The sample operation identities are `Reminder:{ReviewKey}:1` and `Escalation:{ReviewKey}`. The in-memory service checks exact command reuse and rejects a conflicting command for the same key. It does not make side effects durable or exactly-once across process boundaries; production notification/escalation handlers need durable idempotency appropriate to the external capability.

## Completion-before-deadline behavior

`ReviewSlaRaceWorkflow` races an Elsa `Event("ReviewCompleted")` branch with a branch containing the initial Delay, reminder, second Delay, and escalation. The graph is a `While` containing `Fork(WaitAll)`. The winning branch calls Elsa `Break`, which cancels the remaining branch's outstanding wait in the tested runtime.

The test resumes the event bookmark while the Delay bookmark is pending. It then proves the workflow is finished, the timer bookmark is gone, action count remains zero, and no timer action appears after the original deadline while the process stays alive. This is evidence for this graph/runtime path, not a general guarantee about every graph shape or external scheduler.

## Process restart

The dedicated process harness runs as separate OS processes and uses the SQL Server test database:

- **Graceful restart before due:** Process A starts a workflow with a 12-second initial Delay, commits its `Running` / `Suspended` state and exits. Process B starts a new host, inspects the pending bookmark without consuming it, and exits. Process C starts after the deadline and finishes the reminder/escalation sequence. Process D starts after completion and sees stable finished state, outputs, and no bookmark. The tests assert distinct process IDs between phases.
- **Abrupt termination and overdue startup:** Process A starts a 7-second timer and remains alive after suspension. A separate read-only host confirms the management instance and runtime bookmark rows and confirms the due time has not passed. The parent kills Process A. After the due time, Process B starts Elsa, waits for recovery, and observes `Finished` / `Finished`, one reminder, one escalation, no incidents, and no bookmark. The test checks that completion occurs within a 20-second catch-up bound after Process B starts.

The kill is deliberately after persistence has been confirmed. This does not inject a crash during a database commit or an active side effect.

## Overdue-on-startup behavior

In the abrupt-termination test, Elsa was offline until at least 750 ms after the persisted `ResumeAt`. A newly started process completed the overdue workflow without a manual bookmark resume call. This is the observed behavior for the tested SQL-backed `Delay` bookmark and one runtime process. The test's recovery timeout is 45 seconds and its prompt catch-up assertion is 20 seconds; it does not establish an exact firing latency.

## Duplicate behavior

After completion, a new inspect-only process observed a finished workflow, stable reminder/escalation outputs, and no pending bookmark. Starting that host did not replay the completed timer path in the tested single-node setup.

Separately, service-level testing proved same-command idempotency and conflicting-key rejection in `InMemoryReviewSlaActionService`. Because the service's operation journal is in memory, the experiment does not prove side-effect de-duplication after a process restart. Elsa timer consumption and finished workflow state prevent this tested completed timer from being scheduled again; application side effects still need their own durable idempotency if duplicate invocation is possible.

## Multiple workflow isolation

The in-process test runs two independently keyed workflows with different delays. The short timer completes first while the longer timer remains suspended with its own persisted bookmark and unchanged outputs. The process-resume ELSA-10 evidence also covers isolation of persisted instances, but ELSA-11's process timer cases exercise one timed instance at a time.

## Side-effect considerations

The application service owns operation identity and duplicate semantics; Elsa owns the timer and orchestration. A workflow can be recovered after a deadline, but that does not make an arbitrary email, message, or EDMS update exactly-once. Use a durable operation journal/idempotency key at the application boundary and reconcile ambiguous outcomes when needed. ELSA-11 does not add a real notification provider.

## Elsa vs periodic scanner

For a deadline that belongs to one workflow instance and should continue a defined sequence, this experiment supports using Elsa's persisted Delay bookmark as the orchestration timer in a single-active-runtime deployment. It also shows that host startup can restore an overdue timer from SQL in the tested setup.

A periodic database scanner remains useful for cross-workflow overdue queries, operational reports, reconciliation, or policies that are naturally expressed as set-based queries. This experiment did not benchmark a scanner against Elsa timers. The scheduler used here is local to one process; it does not establish clustered ownership, multi-node coordination, or distributed exactly-once delivery. Do not replace a production scanner solely from this result if those operational guarantees are required.

## Confirmed from Elsa 3.8.4 source

- The `Elsa.Scheduling` module registers the scheduling feature and its default local scheduler when `UseScheduling` is used. `Delay` uses the Activity execution context's delay/bookmark mechanism and stores a `DelayPayload` with `ResumeAt` based on Elsa's `ISystemClock`.
- The local scheduler is in-memory. `CreateSchedulesStartupTask` rebuilds schedules from persisted runtime bookmarks when the host starts. This explains how the tested SQL-backed Delay can be recovered after a host/process restart; it does not make the scheduler clustered.
- `PastDueScheduleStaggerer` has source defaults of a 1 ms minimum delay, 50 ms stagger interval, and 5-minute stagger window. This is catch-up scheduling behavior, not a promise of exact deadline firing.
- Elsa's tagged integration test uses a `While` with a `Fork`, where a branch calls `Break` to cancel remaining branch work. The experiment applied that native pattern to an Event-versus-Delay race and verified the resulting bookmark removal at runtime.

## EDMS implication

EDMS task states such as `Assigned`, `DueSoon`, `Overdue`, `ReminderSent`, `Escalated`, and `Completed` remain application/domain state. Elsa can own when a reminder or escalation transition runs and which continuation follows. For a production review task, persist the workflow's timer state and keep notification/escalation commands idempotent in application services. A database scanner can still support portfolio-wide SLA views and reconciliation.

## Distributed limitations

Only one active Elsa runtime was used at a time. The test does not establish clustered scheduling correctness, competing timer ownership, multi-node coordination, exactly-once timer firing, distributed side-effect de-duplication, or recovery from a process failure while an Activity is in the middle of a side effect. Real email, business calendars/time zones, dynamic deadline changes, and long-duration SLA policy were not tested. Tests used real short delays; no fake clock was installed, although the tagged `Delay` implementation reads `ISystemClock`.

## Limitations

- SQL-backed tests require a SQL Server connection with permission to create and drop unique ElsaLab databases. They are categorized as `SlaTimer`, `ProcessRestart`, and `SqlServer`.
- Restart tests confirm the bookmark was committed before exit/kill. They do not test loss during an uncommitted persistence operation.
- The action service is deliberately in memory. Only local same-command idempotency was tested; production durability belongs to the application service or its backing store.
- The test suite covers one winner in the review-versus-timer race and two independent short timers. It is not a load, calendar, time-zone, or clustered scheduling test.

## Executable evidence

- [Linear timer workflow](../../src/ElsaLab.Runner/Workflows/ReviewSlaWorkflow.cs)
- [Review-completion versus timer workflow](../../src/ElsaLab.Runner/Workflows/ReviewSlaRaceWorkflow.cs)
- [Thin reminder/escalation Activities](../../src/ElsaLab.Runner/Activities/ReviewSlaActivities.cs)
- [Application action service](../../src/ElsaLab.Runner/Services/ReviewSlaActionService.cs)
- [SQL timer and race tests](../../tests/ElsaLab.Tests/ReviewSlaTimerTests.cs): `Delay_PersistsBookmarkAndBlocksDownstreamBeforeDueTime`, `DelayFires_ReminderThenEscalationRunOnce_InOrder_AndFinish`, `ReviewCompletedBeforeDeadline_CancelsTimerBookmark_AndPreventsReminderOrEscalation`, `TwoSlaWorkflows_KeepIndependentDeadlinesAndActions`, and `ReviewSlaActionService_IsIdempotentForSameCommand_AndRejectsConflictingKeyReuse`
- [Separate-process timer harness](../../tests/ElsaLab.ProcessHarness/SlaTimerProcessHarness.cs)
- [Graceful, abrupt-kill, and overdue process tests](../../tests/ElsaLab.Tests/SlaTimerProcessRestartTests.cs): `GracefulRestartBeforeDue_RestoresTimerAndContinuesOnce` and `AbruptKillAfterSqlCommit_OverdueTimerIsRecoveredOnStartup`

## Elsa source references

All source links are pinned to the Elsa 3.8.4 tag.

- [`Delay`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Activities/Delay.cs)
- [Delay execution-context extension](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Extensions/DelayActivityExecutionContextExtensions.cs)
- [Delay bookmark payload](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Bookmarks/DelayPayload.cs)
- [Scheduling feature and `UseScheduling` setup](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Features/SchedulingFeature.cs)
- [Local scheduler](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Services/LocalScheduler.cs)
- [Startup schedule restoration](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/StartupTasks/CreateSchedulesStartupTask.cs)
- [Past-due staggerer](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Services/PastDueScheduleStaggerer.cs)
- [Scheduling options](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Scheduling/Options/SchedulingOptions.cs)
- [`Fork`, `While`, and `Break` Activities](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/src/modules/Elsa.Workflows.Core/Activities)
- [Tagged integration test for breaking out of a fork in a While](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/BlockingAndBreaking)
