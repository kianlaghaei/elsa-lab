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

## Reference

These pages synthesize repeated knowledge and point back to experiments for evidence.

- [Workflow data](reference/workflow-data.md)
- [Activities and dependency injection](reference/activities-and-di.md)
- [Flowchart routing](reference/flowchart-routing.md)
- [Flowchart cycles](reference/flowchart-cycles.md)

## Patterns

- [Thin Activity calling an application service](patterns/activity-application-service.md) — based on ELSA-03.

## Version Notes

- [Elsa 3.8.4 baseline](versions/elsa-3.8.4.md)
- All five completed experiments were verified against Elsa 3.8.4 and .NET 10 (`net10.0`). Re-run relevant experiments before relying on these findings after an Elsa upgrade.

## Current Knowledge Coverage

| Concept | Verified in |
|---|---|
| Code-first workflow execution | ELSA-01 |
| Workflow inputs, variables, outputs, and typed result | ELSA-02 |
| Custom Activity, DI, Activity output, cancellation-token propagation, and tested Activity reuse | ELSA-03 |
| Conditional routing with `FlowDecision` | ELSA-04 |
| Loops and revision cycles | ELSA-05 |
| Parallel execution and join | Not yet tested (ELSA-06) |
| Bookmarks / blocking activities | Not yet tested (ELSA-07) |
| External events and wait/resume | Not yet tested (ELSA-08) |
| Persistence | Not yet tested (ELSA-09) |
| Process restart and resume | Not yet tested (ELSA-10) |
| Timers, delay, SLA, and escalation | Not yet tested (ELSA-11) |
| Failure handling and retry | Not yet tested (ELSA-12) |
| Workflow versioning | Not yet tested (ELSA-13) |
| End-to-end workflow cancellation/interruption lifecycle | Not yet tested (ELSA-14); token propagation to a service was tested in ELSA-03 |
| Broad execution history/audit/observability | Not yet tested (ELSA-15); selected in-process journal fields were inspected in ELSA-02 through ELSA-04 |
| State machine evaluation | Not yet tested (ELSA-16) |
| Human Task backend | Not yet tested (ELSA-17) |
| Load and concurrency | Not yet tested (ELSA-18) |
| EDMS-like end-to-end workflow | Not yet tested (ELSA-19) |

## Executable Evidence

- Current runner and code-first workflow: [`src/ElsaLab.Runner`](../src/ElsaLab.Runner/)
- Current automated tests: [`tests/ElsaLab.Tests`](../tests/ElsaLab.Tests/)
- ELSA-01 and ELSA-02 implementations were later replaced as the experiment progressed; their experiment pages link to the exact source and test files at their implementation commits.
