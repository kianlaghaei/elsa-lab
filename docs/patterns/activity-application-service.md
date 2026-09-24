# Pattern: Elsa Activity Calling an Application Service

**Basis:** observed in [ELSA-03](../experiments/ELSA-03-custom-activity-di.md) against Elsa 3.8.4. This is a small integration pattern demonstrated by the lab, not a general enterprise architecture framework.

```text
Elsa Workflow
      ↓
Thin Elsa Activity
      ↓
Application Service
      ↓
Domain / Infrastructure
```

## Responsibilities demonstrated

- The workflow binds values to a typed Activity input and sequences/orchestrates the operation.
- The Activity reads evaluated input values, resolves `IDocumentProcessingService` through `ActivityExecutionContext.GetRequiredService<T>()`, passes the runtime cancellation token, and maps the service result to native Elsa output ports.
- The application service owns the document validation/reference-generation behavior. The Activity does not contain that business implementation.
- Downstream workflow logic consumes named Activity outputs through Elsa's tested `GetOutput<T>` path and variables.

The runner registers the service as scoped through Microsoft DI and resolves/runs the workflow runner within a scope. The fake-service test replaces the application service registration and continues to use the real Elsa runtime.

## State and lifetime rule

One test built a workflow graph, ran it twice, and observed the same Activity definition instance in both executions. Do not store mutable per-run data on Activity fields. Keep run-specific values in execution locals and Elsa workflow/runtime data. This finding applies to the graph path exercised; other host activation paths were not tested.

## EDMS/FLOREX application ideas

**Inference, not implemented:** operations such as `CreateTransmittal`, `ConsolidateComments`, `RegisterRevision`, `SendDocument`, and `ValidateDocument` could use a typed Activity as the orchestration adapter and a normal service for application behavior. The lab did not implement persistence, transactions, idempotency, security policy, or external-system integration for those operations.

## Executable evidence

- [ELSA-03 experiment record](../experiments/ELSA-03-custom-activity-di.md)
- [Custom Activity source at ELSA-03](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/src/ElsaLab.Runner/Activities/RegisterDocumentActivity.cs)
- [ELSA-03 tests](https://github.com/kianlaghaei/elsa-lab/blob/bf50d68bfde14ba0a51a73a5ff134b4bc878148c/tests/ElsaLab.Tests/DocumentProcessingWorkflowTests.cs)
