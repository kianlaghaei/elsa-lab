# ELSA-12 — Failure Handling, Retry and Incidents

| Metadata | Value |
|---|---|
| Experiment | ELSA-12 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Database | SQL Server 2022 |
| Verified date | 2026-09-25 |
| Implementation commit | pending feature commit |

## Observed

- The custom `PublishDocumentActivity` is a `CodeActivity` and `IResilientActivity`. It resolves `IDocumentPublicationService`, maps typed Activity inputs to one command, calls Elsa's `IResilientActivityInvoker`, propagates `ActivityExecutionContext.CancellationToken`, and maps the service result to Elsa output ports. The retry policy and business outcome are outside the Activity instance; the workflow graph and Activity hold no per-run mutable fields.
- A registered deterministic retry strategy with two retries and a fixed 5 ms delay invoked the service three times when its first two calls threw `TransientDocumentServiceException`. The workflow ended `Finished` / `Finished`; the Activity and downstream `AfterPublication` each executed once; there were no incidents; its workflow outputs were `Applied` and `Client`.
- Elsa recorded two retry records for the successful run. They had zero-based attempt numbers `0`, `1`, a 5 ms delay, the same Activity instance ID as the executing Activity context, the Activity definition ID, and the workflow instance ID. The record details included the exception and operation ID.
- `DocumentValidationException` was not classified as transient: it caused one service call, no retries, a faulted Activity, one incident, and no downstream Activity. With the tested custom host's default incident strategy, the result was `WorkflowStatus.Finished` and `WorkflowSubStatus.Faulted`.
- Exhausting two retries for a transient exception made three total service calls. Elsa recorded a faulted Activity and an incident with message `temporary publication outage`; downstream work did not execute. The workflow state was `Finished` / `Faulted`.
- In this Elsa 3.8.4 path, no retry records were returned after exhaustion. This matched source order: `ResilientActivityInvoker` calls its recorder only after `pipeline.ExecuteAsync` returns successfully; an exhausted pipeline throws before that call. Retry telemetry is collected in Activity transient properties while the invocation is running, and those transient properties are not persisted.
- With `ContinueWithIncidentsStrategy` on the tested linear `Sequence`, the Activity fault and incident remained, `AfterPublication` did not execute, there was no workflow output or bookmark, and Elsa returned `Running` / `Suspended`. This is not equivalent to skipping the failed Activity and advancing the Sequence; the observed instance has no bookmark that our experiment can resume.
- The SQL Server tests reloaded both successful retry history and faulted workflow state through a fresh service provider. Successful attempts were returned by `IRetryAttemptReader` backed by Elsa's SQL `IActivityExecutionStore`. The process test then showed the same faulted state from a separate OS process (different PID).
- The exception message, Activity ID, and incident remained after SQL/process reload. The in-memory incident held `DocumentValidationException`; SQL rehydration returned `ExceptionState.Type = System.Exception` while retaining the message. The concrete exception CLR type did not round-trip in this tested provider path.
- In the apply-then-throw scenario, publication was recorded by the application service before the first call threw. Elsa called the Activity/service again using the same `OperationId`; the service returned `AlreadyApplied`. There were two service calls and one applied operation, and the workflow then finished normally. Reusing that operation ID for another destination was rejected by the service.
- Cancellation reached the service as the same token. Cancellation did not retry: there was one service call, the Activity context was `Faulted`, and the result was `Finished` / `Cancelled` with no downstream execution.

## Elsa resilience model

The implementation uses `Elsa.Resilience` 3.8.4 (which brings in `Elsa.Resilience.Core` 3.8.4 and Polly 8.6.6 transitively). `UseResilience` registers the native resilient Activity services. `PublishDocumentActivity` supplies a `ResilienceStrategyConfig` in `CustomProperties["resilienceStrategy"]`; the strategy is selected by identifier from Elsa's configured `Resilience:Strategies` source.

The Activity calls `IResilientActivityInvoker.InvokeAsync`. The configured `DocumentPublicationRetryStrategy` uses Polly's `RetryStrategyOptions<T>` with `MaxRetryAttempts = 2`, a constant 5 ms delay, no jitter, and an exception predicate. That predicate resolves Elsa's `ITransientExceptionDetector` from the `ActivityExecutionContext` stored in the Polly `ResilienceContext`. A registered `ITransientExceptionStrategy` classifies only `TransientDocumentServiceException` as transient. Cancellation requested through the resilience context is excluded before classification.

In this installed Elsa version, registering an `ITransientExceptionStrategy` alone does not cause arbitrary Activity failures to retry. The selected Polly strategy must consult the detector (or provide an equivalent explicit exception predicate). The tested policy does not retry validation/business exceptions.

## Transient failure

Two temporary publication failures followed by success produced three service calls, two recorded retry attempts, one completed Activity execution context, one downstream execution, and a successful workflow. The attempt numbers are zero-based in the Elsa `RetryAttemptRecord` model. The delay field reported 5 ms for each attempt. These assertions use Elsa's retry reader as evidence as well as application-service call count.

## Permanent failure

The `DocumentValidationException` was not transient under the configured detector. It was called once and passed to normal Elsa Activity exception/incident handling. It was not transformed into success or silently ignored by `FaultStrategy`.

## Retry exhaustion

An always-transient fake service was called three times (initial call plus two retries). The final exception was handled as a workflow incident. The Activity was `Faulted`, the downstream Activity was absent, and the workflow's top-level status was `Finished` while its substatus was `Faulted`.

The normal retry recorder did not emit durable attempt records for this failed pipeline, because Elsa invokes the recorder only after the pipeline has returned a successful result. The attempts are therefore not an audit trail of failed/exhausted policy calls in this observed path. If EDMS needs a complete failed-attempt audit, it must retain that evidence in an application-owned operational store or use a deliberate Elsa recorder extension; this experiment does not add one.

## Retry attempt recording

The default `ActivityExecutionContextRetryAttemptRecorder` writes `RetryAttemptRecord` values to the Activity execution context's `Properties["RetryAttempts"]`. The default reader loads an Activity execution record from `IActivityExecutionStore` and converts that property. In SQL, a new provider returned both records after the workflow completed, demonstrating that the successful Activity execution record and its properties survived persistence. No custom retry table or recorder was added.

Recorded fields observed by the test include `ActivityInstanceId`, `ActivityId`, `WorkflowInstanceId`, `AttemptNumber`, `RetryDelay`, and custom details (`attemptNumber`, exception type/message, operation ID). Elsa's record model has an identity ID as well. Failed/exhausted retries were absent from this recorder in the tested source/runtime path, as described above.

## FaultStrategy

The ordinary Elsa host did not configure a custom `IncidentOptions` default. `DefaultIncidentStrategyResolver` therefore selected `FaultStrategy`. On the tested Activity exception, it left the Activity faulted, did not run `AfterPublication`, retained an incident, and returned `Finished` / `Faulted`. Both workflow status and substatus must be checked; the top-level `Finished` value alone did not indicate success.

## ContinueWithIncidentsStrategy

The equivalent publication workflow explicitly set `builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy)`. The source strategy itself does not transition the workflow into `Faulted`. In this linear Sequence, however, the faulted child was not treated as a completed child: Elsa left the workflow `Running` / `Suspended`, preserved its incident, and did not schedule the next Sequence Activity or named output. No bookmark existed. This result is specific to this linear graph/host path; it does not claim every graph topology responds identically. An operational repair/resume path for this state was not tested.

## Incident persistence

`WorkflowState.Incidents` contained an `ActivityIncident` with Activity ID, node ID, Activity type, message, timestamp, and serializable exception state. SQL Server rehydration retained the Activity ID and exception message. The tested JSON round trip materialized the exception type as `System.Exception`, not the original custom exception type; consumers should not use persisted exception type identity as a reliable domain classification without verifying the provider/serializer contract.

## Process restart with faulted state

Process A ran the permanent failure against a unique SQL Server database and exited normally after the run committed. Process B was a separately launched `ElsaLab.ProcessHarness` OS process with a different PID. It loaded the same workflow instance and incident from the SQL-backed store without retrying or altering it. This proves persistence of the resulting fault state across an actual process exit; it does not prove an automatic retry or repair after restart.

## Apply-then-throw ambiguity

The test service applied the publication operation and then simulated loss of its acknowledgement by throwing a transient exception. Elsa could only observe a failed Activity invocation, so its policy retried the call. The same operation identity allowed the service to identify the already-applied command and return `AlreadyApplied`. The physical/logical effect occurred once even though the Activity called the service twice.

The application service also rejected a materially different command under the same operation ID. This is a narrow deterministic fake; it is not a distributed idempotency framework.

## Application-side idempotency

**Verified rule for this pattern:** Polly/Elsa retry repeats the Activity delegate; Elsa does not deduplicate arbitrary service or external side effects. External commands such as publish, move, transmittal, notification, vendor/client send, or revision archive need an idempotent operation key or an inherently idempotent effect. The service must reject an idempotency key reused for different command data. This aligns with [EDMS-FIT-01](../fit-tests/EDMS-FIT-01-document-storage.md), which proved the same principle for document movement.

## Manual retry behavior

Not tested. Elsa 3.8.4 source includes a [workflow retry alteration endpoint](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Alterations/Endpoints/Workflows/Retry/Endpoint.cs) and [`ScheduleActivityHandler`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Alterations/AlterationHandlers/ScheduleActivityHandler.cs), which are distinct from the Polly invocation retry tested here. This experiment does not establish how manual retry updates incidents, Activity state, workflow status, or side-effect history.

## EDMS failure classification

**Inference for future EDMS services:** classify temporary network/provider timeouts and temporary storage unavailability as transient only when repeating the same command is safe. Treat invalid revision transitions, missing required metadata, and authorization/business-rule failures as non-retriable. The EDMS service owns that classification; Elsa applies the configured policy. Ambiguous timeouts after an external system may have committed remain retryable only with application-side idempotency/correlation.

## Confirmed from Elsa 3.8.4 source

- `IResilientActivity` adds `CollectRetryDetails`; `IResilientActivityInvoker` invokes the configured strategy or calls its action directly if none resolves.
- `ResilientActivityInvoker` places the current `ActivityExecutionContext` into Polly's `ResilienceContext`, creates the Polly pipeline, executes the Activity action, and records accumulated attempts only after that execution returns successfully.
- `ActivityExecutionContextRetryAttemptRecorder` stores attempts in `Properties["RetryAttempts"]`; `ActivityExecutionContextRetryAttemptReader` reads them from Elsa's `IActivityExecutionStore` record.
- `DefaultIncidentStrategyResolver` precedence is workflow `IncidentStrategyType`, `IncidentOptions.DefaultIncidentStrategy`, then `FaultStrategy`.
- `FaultStrategy` transitions to `WorkflowSubStatus.Faulted` when possible. `ContinueWithIncidentsStrategy` does nothing to the substatus. Activity exception middleware first faults the Activity context, then invokes the resolved strategy.
- `ActivityIncident` includes Activity identity/type, message, `ExceptionState`, and timestamp. `ExceptionState` is a serializable projection with exception type, message, stack trace, inner state, and optional privacy-safe metadata.

These implementation details were inspected in Elsa's `3.8.4` tag. The runtime findings above are limited to the tested workflow and SQL Server host.

## Limitations

- Retry timing used a short real Polly delay; no jitter, exponential backoff, circuit breaker, load, or concurrent retry policy was tested.
- Cancellation was tested for one in-flight service call only. Broader cancellation/interruption belongs to ELSA-14.
- SQL and process tests used one local SQL Server 2022 runtime at a time. They do not establish distributed retry coordination or exactly-once delivery.
- Incident persistence was tested after a completed Activity failure. Process death during an active Activity or before the commit is not covered.
- `ContinueWithIncidentsStrategy` produced a suspended workflow without a bookmark in this linear Sequence. Manual repair/retry behavior is untested.
- Manual workflow alteration retry, workflow restart, recovery from interrupted Activities, and incident clearing/retention policy were not tested.
- Exception `Type` normalized to `System.Exception` after the tested SQL round trip; other persistence serializers/providers were not evaluated.

## Executable evidence

- [Activity](../../src/ElsaLab.Runner/Activities/PublishDocumentActivity.cs)
- [Application-service contract and idempotent in-memory implementation](../../src/ElsaLab.Runner/Services/IDocumentPublicationService.cs) and [implementation](../../src/ElsaLab.Runner/Services/InMemoryDocumentPublicationService.cs)
- [Retry strategy and transient detector adapter](../../src/ElsaLab.Runner/Services/DocumentPublicationResilience.cs)
- [Code-first workflows](../../src/ElsaLab.Runner/Workflows/DocumentPublicationWorkflow.cs)
- [In-memory, SQL round-trip, retry, incident, idempotency and cancellation tests](../../tests/ElsaLab.Tests/DocumentPublicationResilienceTests.cs)
- [Separate-process fault persistence harness](../../tests/ElsaLab.ProcessHarness/FailureProcessHarness.cs) and [test](../../tests/ElsaLab.Tests/DocumentPublicationProcessRestartTests.cs)
- [Manual retry demonstration](../../src/ElsaLab.Runner/Services/DocumentPublicationDemo.cs)

## Elsa source references

- [`Elsa.Resilience` package source](https://github.com/elsa-workflows/elsa-core/tree/3.8.4/src/modules/Elsa.Resilience)
- [`UseResilience`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience/Extensions/ModuleExtensions.cs)
- [`IResilientActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Contracts/IResilientActivity.cs), [`IResilientActivityInvoker`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Contracts/IResilientActivityInvoker.cs), and [`IResilienceStrategy`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Contracts/IResilienceStrategy.cs)
- [`ResilientActivityInvoker`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Services/ResilientActivityInvoker.cs)
- [`ITransientExceptionDetector`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Contracts/ITransientExceptionDetector.cs), [`ITransientExceptionStrategy`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Contracts/ITransientExceptionStrategy.cs), and [`TransientExceptionDetector`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Services/TransientExceptionDetector.cs)
- [`RetryAttemptRecord`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience.Core/Entities/RetryAttemptRecord.cs), [`ActivityExecutionContextRetryAttemptRecorder`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience/Recorders/ActivityExecutionContextRetryAttemptRecorder.cs), and [`ActivityExecutionContextRetryAttemptReader`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Resilience/Recorders/ActivityExecutionContextRetryAttemptReader.cs)
- [`FaultStrategy`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/IncidentStrategies/FaultStrategy.cs), [`ContinueWithIncidentsStrategy`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/IncidentStrategies/ContinueWithIncidentsStrategy.cs), and [`DefaultIncidentStrategyResolver`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/DefaultIncidentStrategyResolver.cs)
- [`Activity exception middleware`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Middleware/Activities/ExceptionHandlingMiddleware.cs), [`ActivityIncident`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/ActivityIncident.cs), and [`ExceptionState`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/State/ExceptionState.cs)
- Elsa integration evidence: [`FlowSendHttpRequestResilienceTests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Resilience.IntegrationTests/FlowSendHttpRequestResilienceTests.cs) and [`IncidentStrategyTests`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/Incidents/IncidentStrategyTests.cs)
