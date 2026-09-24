# ElsaLab Learnings

## ELSA-01 — Basic Code-First Execution

### What Elsa handled

Elsa executed the two activities in order and reported the workflow as finished. No custom scheduling or execution logic was needed.

### Elsa concepts learned

Used `WorkflowBase`, `IWorkflowBuilder`, `Sequence`, and `WriteLine` to define the workflow; registered it through `AddElsa(...AddWorkflow<...>())`; resolved and called `IWorkflowRunner`; inspected `RunWorkflowResult.WorkflowState.Status`. `ServiceCollection` supplied the host's dependency injection container.

### Runtime behavior

The runner executed synchronously in the console process. It printed `Document received` followed by `Document processing started`, and the returned status was `Finished`. The automated test observed the same finished state through Elsa's runner.

### Surprises

`IWorkflowRunner` is in the `Elsa.Workflows` namespace in the installed 3.8.4 package. `Elsa.Workflows.Core` contains the workflow types but did not provide the documented `AddElsa()` registration extension by itself. The official `Elsa` 3.8.4 bundle provided the bootstrap and aligned Core, Management, Runtime, and API Common package dependencies. The bundle does not add `Elsa.Server.Api` or expose REST endpoints by itself.

### Limitations

This experiment covers only an in-process, synchronous workflow. It does not test persistence, recovery, inputs/outputs, long-running execution, or activity dependency injection.

### Production implications

This confirms that a small, source-controlled workflow can run directly through Elsa's runtime and expose its execution state. It is a useful basis for synchronous orchestration in EDMS/FLOREX. It does not yet establish whether Elsa meets durable or human-driven process requirements; those remain later roadmap experiments.