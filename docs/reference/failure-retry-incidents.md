# Failure, Retry and Incidents

This short guide summarizes the tested Elsa 3.8.4 behavior in [ELSA-12](../experiments/ELSA-12-failure-retry-incidents.md). The experiment page contains the complete evidence and limits.

## Resilience is an Activity invocation policy

A custom `IResilientActivity` calls `IResilientActivityInvoker.InvokeAsync`. Elsa resolves the strategy config from the Activity's `CustomProperties["resilienceStrategy"]`, runs the supplied delegate through the selected Polly pipeline, and uses `CollectRetryDetails` to form records. With no strategy resolved, the invoker calls the Activity action directly. The sample uses `Elsa.Resilience` 3.8.4, a fixed short retry delay, and a policy predicate that explicitly consults Elsa's `ITransientExceptionDetector`.

In the test policy, a transient failure twice then success yielded three service calls and two retry records numbered 0 and 1. A permanent validation exception was called once. Retry policies repeat a delegate; they do not guarantee once-only effects.

## Retry records have a success-path boundary

Elsa's default recorder stores attempt records under the successful Activity execution context's `Properties["RetryAttempts"]`; the reader gets them through `IActivityExecutionStore`. The successful retry history survived SQL provider reconstruction in ELSA-12. Source and runtime tests also showed a limit: the invoker calls the recorder after the Polly pipeline returns. When retry exhaustion throws, that recorder call is skipped, so the normal reader has no exhausted-attempt history for this run.

## Incidents are a separate failure policy

The host tested with no incident override resolved to `FaultStrategy`, the resolver's final fallback. It produced `WorkflowStatus.Finished` / `WorkflowSubStatus.Faulted`, left a faulted Activity and incident, and did not schedule the downstream Sequence Activity. Check workflow substatus and Activity contexts, not only top-level status.

`ContinueWithIncidentsStrategy` leaves the faulted Activity incident in place without marking the workflow faulted. In the tested linear Sequence, it returned `Running` / `Suspended`; downstream did not run and no bookmark existed. Do not interpret the strategy name as unconditional skip-and-continue behavior.

SQL serialization preserved incident Activity identity and message. The tested rehydrated `ExceptionState.Type` was `System.Exception`, while the original in-memory exception state named the custom validation type.

## External side effects require application idempotency

The Activity delegates to a normal service with a stable operation ID. An apply-then-throw retry called the service twice, but the service's operation journal returned `AlreadyApplied` on the second call and retained one effect. A different command reusing the same ID was rejected. This and [EDMS-FIT-01](../fit-tests/EDMS-FIT-01-document-storage.md) support a narrow rule: any side-effecting Activity needs an inherently idempotent operation or application-side idempotency key and command-conflict validation.

This evidence covers a deterministic in-memory service and one SQL-backed Elsa runtime. It does not promise distributed exactly-once delivery or a complete audit of exhausted retry attempts.

## Evidence

- [ELSA-12 experiment](../experiments/ELSA-12-failure-retry-incidents.md)
- [Elsa Activity](../../src/ElsaLab.Runner/Activities/PublishDocumentActivity.cs), [application-service idempotency sample](../../src/ElsaLab.Runner/Services/InMemoryDocumentPublicationService.cs), and [tests](../../tests/ElsaLab.Tests/DocumentPublicationResilienceTests.cs)
