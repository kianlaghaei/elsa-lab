# Elsa 3.8.4 Verification Baseline

| Item | Verified value |
|---|---|
| Elsa version | 3.8.4 |
| Target framework | .NET 10 (`net10.0`) |
| Primary package | `Elsa` 3.8.4 |
| Scheduling package used by ELSA-11 | `Elsa.Scheduling` 3.8.4 |
| Resilience package used by ELSA-12 | `Elsa.Resilience` 3.8.4 (`Elsa.Resilience.Core` 3.8.4 transitively; Polly 8.6.6 transitively) |

The runner uses Elsa's `Elsa` bundle package at 3.8.4. The project does not configure Elsa Server API endpoints, Studio, or a Designer.

## Experiments verified against this baseline

| Experiment | Implementation commit | Knowledge page |
|---|---|---|
| ELSA-01 — Basic Code-First Execution | [`b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c`](https://github.com/kianlaghaei/elsa-lab/commit/b27d78f6aa1222b96f2ccd073d3aa8e617ae6c8c) | [Experiment record](../experiments/ELSA-01-basic-execution.md) |
| ELSA-02 — Workflow Data | [`e4517859c1567b16c4bdfd4a330b62c194fcbc27`](https://github.com/kianlaghaei/elsa-lab/commit/e4517859c1567b16c4bdfd4a330b62c194fcbc27) | [Experiment record](../experiments/ELSA-02-data-model.md) |
| ELSA-03 — Custom Activity + DI | [`bf50d68bfde14ba0a51a73a5ff134b4bc878148c`](https://github.com/kianlaghaei/elsa-lab/commit/bf50d68bfde14ba0a51a73a5ff134b4bc878148c) | [Experiment record](../experiments/ELSA-03-custom-activity-di.md) |
| ELSA-04 — Flowchart Routing | [`24f7d2554b9259806896b9e36c94c62241769f9b`](https://github.com/kianlaghaei/elsa-lab/commit/24f7d2554b9259806896b9e36c94c62241769f9b) | [Experiment record](../experiments/ELSA-04-flowchart-routing.md) |
| ELSA-05 — Loops and Revision Cycles | [`35fe9f8232277bf930c77cc5e47a10509d3e03ed`](https://github.com/kianlaghaei/elsa-lab/commit/35fe9f8232277bf930c77cc5e47a10509d3e03ed) | [Experiment record](../experiments/ELSA-05-revision-cycles.md) |
| ELSA-06 — Parallel Execution + Join | [`47c152954945dce32b75ec3df77443c043653c09`](https://github.com/kianlaghaei/elsa-lab/commit/47c152954945dce32b75ec3df77443c043653c09) | [Experiment record](../experiments/ELSA-06-parallel-join.md) |
| ELSA-07 — Blocking Activity / Bookmark | [`84b1aae1ca41cfa3f12e19569fd5679d9079b8cb`](https://github.com/kianlaghaei/elsa-lab/commit/84b1aae1ca41cfa3f12e19569fd5679d9079b8cb) | [Experiment record](../experiments/ELSA-07-blocking-bookmark.md) |
| ELSA-08 — External Event + Suspend / Resume | [`164256325697f1ca5b9db2e220cf4016715bbd53`](https://github.com/kianlaghaei/elsa-lab/commit/164256325697f1ca5b9db2e220cf4016715bbd53) | [Experiment record](../experiments/ELSA-08-external-resume.md) |
| ELSA-09 — SQL Server Persistence | [`817c3fbc62da4e573bb6722ab1a265008a604dd8`](https://github.com/kianlaghaei/elsa-lab/commit/817c3fbc62da4e573bb6722ab1a265008a604dd8) | [Experiment record](../experiments/ELSA-09-sql-server-persistence.md) |
| ELSA-10 — Process Kill + Restart + Resume | [`0a96a5be0e15204c8aa6a7bd98bf8714910bd6d1`](https://github.com/kianlaghaei/elsa-lab/commit/0a96a5be0e15204c8aa6a7bd98bf8714910bd6d1) | [Experiment record](../experiments/ELSA-10-process-restart-recovery.md) |
| ELSA-11 — Timers / Delay / SLA / Escalation | [`3d475607e78ae3b1483ebac8bc9fccd6e5d9a869`](https://github.com/kianlaghaei/elsa-lab/commit/3d475607e78ae3b1483ebac8bc9fccd6e5d9a869) | [Experiment record](../experiments/ELSA-11-sla-timers-escalation.md) |
| ELSA-12 — Failure Handling + Retry | [`ca6d2e103fc69f9f5b6c1db381c384511089ead0`](https://github.com/kianlaghaei/elsa-lab/commit/ca6d2e103fc69f9f5b6c1db381c384511089ead0) | [Experiment record](../experiments/ELSA-12-failure-retry-incidents.md) |

## Upgrade rule

These findings were observed or checked against Elsa 3.8.4 on .NET 10. When Elsa is upgraded, rerun the relevant experiments and inspect the new version's source before relying on these conclusions. No forward compatibility is claimed by this baseline.
