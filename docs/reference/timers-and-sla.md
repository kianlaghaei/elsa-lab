# Timers and SLA

This reference records the durable timer behavior demonstrated by [ELSA-11](../experiments/ELSA-11-sla-timers-escalation.md) on Elsa 3.8.4. It covers a SQL-backed, single-active-runtime setup; it does not establish clustered scheduling behavior.

## Native Delay

The experiment used package `Elsa.Scheduling` 3.8.4, `UseScheduling`, and the native `Elsa.Scheduling.Activities.Delay`. A Delay creates an Elsa timer bookmark with a `ResumeAt` payload. In the tested runtime, a pending Delay leaves the instance `Running` / `Suspended` and does not execute the downstream Activity before its deadline.

## Persistence and restart

Workflow Management SQL persistence stores the workflow instance and state. Workflow Runtime SQL persistence stores the timer bookmark. The default local scheduler is in-memory; Elsa's startup task rebuilds local schedules from stored bookmarks. ELSA-11 observed a committed timer continue after graceful exit and after an abrupt process kill that occurred after SQL confirmed the bookmark row.

An overdue timer was restored after the next host started. Elsa's 3.8.4 source staggers past-due schedules; a specific firing latency is not guaranteed. See the experiment for exact test bounds and database evidence.

## Early completion and side effects

The tested review-completion race used Elsa `Event`, `Delay`, `While`, `Fork`, and `Break`. The winning review branch removed the pending timer wait, and the runtime remained alive beyond the original due time without sending the reminder or escalation.

Reminder/escalation work should be delegated from thin Activities to application services. Use stable operation identities and durable application-side idempotency for external side effects. ELSA-11's fake service proves only in-memory duplicate handling; Elsa does not make arbitrary email or EDMS operations exactly-once.

## Operational boundary

The experiment supports a workflow-owned timer for a per-instance continuation in a single-active-runtime setup. A periodic database scanner may still be useful for set-based overdue queries, reporting, and reconciliation. This experiment did not compare throughput or validate clustered scheduler ownership, multi-node timer firing, or distributed exactly-once behavior.
