# ElsaLab Learnings

This is the concise index of verified findings. Each statement is scoped to the cited experiment and Elsa version; detailed observations, test evidence, limitations, and source references live in the experiment pages.

## Critical verified findings

- **Observed in ELSA-01:** A code-first `WorkflowBase` with a `Sequence` ran through `IWorkflowRunner` in the console process and returned `WorkflowStatus.Finished`. This proves the small in-process example, not persistence or recovery. See [ELSA-01](experiments/ELSA-01-basic-execution.md).
- **Observed in ELSA-02:** `WithInput<T>` declares a typed workflow input, but the runner caller still supplies a name-keyed `IDictionary<string, object>` through `RunWorkflowOptions.Input`. See [ELSA-02](experiments/ELSA-02-data-model.md).
- **Observed in ELSA-02:** Caller values were available through `WorkflowExecutionContext.Input` but did not appear in `WorkflowState.Input` for the input definitions used in this experiment. The values were not configured for workflow-state storage. See [ELSA-02](experiments/ELSA-02-data-model.md) and the [workflow data reference](reference/workflow-data.md).
- **Observed in ELSA-02 and ELSA-03:** Named workflow outputs in `WorkflowState.Output`, `WorkflowBase<TResult>.Result`, and custom activity output ports are distinct data paths. See [ELSA-02](experiments/ELSA-02-data-model.md), [ELSA-03](experiments/ELSA-03-custom-activity-di.md), and the [workflow data reference](reference/workflow-data.md).
- **Observed in ELSA-03:** In the code-first graph tested, Elsa reused the same custom Activity definition instance across two runs of one built `WorkflowGraph`. Do not store per-run mutable state on Activity instances; this observation is scoped to the tested graph path. See [ELSA-03](experiments/ELSA-03-custom-activity-di.md) and [activities and DI](reference/activities-and-di.md).
- **Observed in ELSA-03:** The custom Activity resolved a scoped application service through `ActivityExecutionContext.GetRequiredService<T>()` and passed it Elsa's `CancellationToken`. A fake service was substituted through normal .NET DI while the real Elsa runtime remained in use. See [ELSA-03](experiments/ELSA-03-custom-activity-di.md) and the [activity/application-service pattern](patterns/activity-application-service.md).
- **Observed in ELSA-03 and ELSA-04:** Top-level `WorkflowStatus.Finished` alone is insufficient evidence of a successful execution; the tested assertions also check `WorkflowExecutionContext.SubStatus` and faulted activity contexts. See [ELSA-03](experiments/ELSA-03-custom-activity-di.md) and [ELSA-04](experiments/ELSA-04-flowchart-routing.md).
- **Observed in ELSA-04:** `FlowDecision` records a `True` or `False` outcome; only the selected branch appeared in journal activity contexts, and the two tested routes converged on the shared completion Sequence. See [ELSA-04](experiments/ELSA-04-flowchart-routing.md) and [flowchart routing](reference/flowchart-routing.md).

## Reference guides

- [Workflow data](reference/workflow-data.md)
- [Activities and dependency injection](reference/activities-and-di.md)
- [Flowchart routing](reference/flowchart-routing.md)
- [Activity/application-service pattern](patterns/activity-application-service.md)
- [Verified Elsa 3.8.4 baseline](versions/elsa-3.8.4.md)
- [Documentation index and untested coverage](INDEX.md)
