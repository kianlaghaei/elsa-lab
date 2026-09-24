# EDMS-FIT-01 — Document Storage and Workflow Side Effects

| Metadata | Value |
|---|---|
| Fit test | EDMS-FIT-01 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`795b51ed4410147eeae07521cb57a13e56d3463f`](https://github.com/kianlaghaei/elsa-lab/commit/795b51ed4410147eeae07521cb57a13e56d3463f) |

This is an EDMS architecture fit test, separate from the numbered Elsa roadmap. It uses physical movement in an isolated temporary directory to check the orchestration boundary; it is not a production storage design.

## Observed

- A native code-first Elsa `Flowchart` used `FlowDecision` to select `Approved` or `NeedsRevision`. The decision journal contained only the selected `True` or `False` outcome, and the non-selected move Activity did not appear in that run's journal.
- The selected `MoveDocumentActivity` ran once. It was an Elsa `CodeActivity` with typed `Input<T>` ports, resolved `IDocumentStorageService` from `ActivityExecutionContext`, passed `ActivityExecutionContext.CancellationToken`, and set native `Output<T>` ports. Elsa named workflow outputs exposed the final location, operation status, final path, and operation ID through `WorkflowState.Output`.
- The service mapped closed logical locations to directories under one configured root. No caller-provided filesystem path was accepted. A path-like document number was rejected before storage changed.
- For `DPC-10-ME-0001`, revision 2, the approved test observed the source file before execution, an absent source afterward, one destination file, unchanged contents, and metadata updated to `LogicalLocation=Approved`, `Status=Approved`, and the operation ID. The `NeedsRevision` route moved the file to `revision-required` and updated metadata correspondingly.
- Both paths ended with `WorkflowStatus.Finished` and `WorkflowSubStatus.Finished`; the journal had no faulted Activity execution contexts.
- The retry-approximation test ran the same Elsa `WorkflowGraph` twice with the same inputs and deterministic operation ID. The first service call returned `Applied`; the second returned `AlreadyApplied`. The repository retained one logical operation and two call-attempt records. There was one destination file, no source file, the same bytes, and unchanged final metadata.
- Reusing the operation ID with the same document and source but a different destination threw `InvalidOperationException`. The existing file, metadata, and single logical operation remained unchanged.
- A repeated operation also rejected a destination whose bytes had been changed after the first successful move. It did not overwrite those unexpected bytes or append a second attempt.
- Each recorded storage command included Elsa's workflow execution ID, Activity ID/name, and Activity execution-context ID. The repeated Activity graph node retained its definition ID while the separate workflow executions had distinct runtime identities.
- The cancellation-token test compared the token supplied to the runner with the exact token observed by the storage service.

## Elsa responsibility

Elsa owned the workflow inputs, decision, graph route, Activity scheduling, Activity input/output registers, named workflow outputs, cancellation token, status, and execution journal. The code-first workflow contains no `System.IO` calls and does not implement file movement or idempotency.

## EDMS responsibility

`IDocumentStorageService` and its filesystem adapter owned logical-location validation, mapping to configured directories, filename validation, source/destination checks, physical movement, content-integrity checking for duplicate operations, idempotency-key conflict detection, and updating the small in-memory document metadata/operation records. The Activity only mapped resolved Elsa inputs to the service command and mapped the service result to Elsa outputs.

The test adapter accepts the closed `DocumentStorageLocation` values `Incoming`, `Approved`, and `RevisionRequired`; it does not take a destination path from workflow input. It rejects filename values outside the fit test's simple alphanumeric, hyphen, and underscore identifier rule.

## Reliability findings

**Observed:** Elsa invoked the service again when the same graph was run again. It did not deduplicate an arbitrary filesystem side effect. The service recognized the same operation ID and equivalent logical command, then verified that the source was absent, the expected destination existed with the recorded SHA-256, and metadata still pointed to that operation before returning `AlreadyApplied`.

**Observed:** an operation ID is not safe to treat as an unconditional success key. Reuse for a materially different command (here, a different destination) was rejected rather than silently accepted.

**Observed:** the in-memory operation journal stores one logical movement record and separate invocation attempts. This distinction lets the retry be visible without recording a second physical movement.

**Inference:** workflow retry/recovery safety requires an application-side idempotent command keyed by a stable operation identity. Elsa's Activity and journal identify an invocation but do not provide exactly-once semantics for external effects. The in-memory ledger only proves this behavior in one process; production idempotency state must survive the applicable recovery boundary.

## Production recommendation

The fit test deliberately moved `incoming/DPC-10-ME-0001-R2.pdf` to an `approved` or `revision-required` folder. This shows Elsa can orchestrate when a move is requested and which logical route is selected. It does not establish that physical movement on each workflow transition is the best EDMS design.

For the future EDMS, prefer considering a stable immutable physical key such as `/storage/{document-id}/{revision-id}/blob`, while database metadata changes `LogicalLocation`, status, ownership, or valid revision. Stable storage avoids coupling a workflow transition to a binary rename/move and can simplify readers, audit, and concurrent references. Physical movement may still be required by customer or operational requirements; in that case keep path mapping and move behavior behind the application service.

Filesystem mutation and database metadata mutation are not one ACID transaction. This adapter moves the file first, then updates metadata and the in-memory operation record. An interruption between these actions can leave the file moved while metadata/journal data is stale. This test does not eliminate that failure window. Production options to investigate include durable idempotent commands, an operation journal, an outbox, reconciliation of storage and metadata, and stable immutable storage. No distributed transaction coordinator or durable store was implemented here.

## Limitations

- Filesystem, metadata, operation journal, and idempotency records are local and in-memory for this fit test. No database, object store, cross-process coordination, crash recovery, or multi-server concurrency was tested.
- The adapter's hash reads and `File.Move` are synchronous filesystem calls. The test proves exact cancellation-token propagation and cancellation checks before the move, not interruption of a move already in progress.
- The test uses small deterministic text bytes with a `.pdf` filename. It does not parse or validate PDFs, permissions, virus scans, retention, encryption, or storage-provider semantics.
- The consistency gap between the physical move and metadata update remains possible; no injected mid-operation failure or reconciliation was tested.
- The operation identity is deterministic for this sample workflow and the service checks command equivalence. A production identity scheme and retention period need application design.
- ELSA-08 external event resume remains TODO. This fit test does not change any numbered ELSA roadmap status.

## Executable evidence

- Elsa custom Activity: [MoveDocumentActivity.cs](../../src/ElsaLab.Runner/Activities/MoveDocumentActivity.cs)
- Code-first Flowchart and named outputs: [DocumentStorageWorkflow.cs](../../src/ElsaLab.Runner/Workflows/DocumentStorageWorkflow.cs)
- Storage service contract and filesystem adapter: [IDocumentStorageService.cs](../../src/ElsaLab.Runner/Services/IDocumentStorageService.cs), [FileSystemDocumentStorageService.cs](../../src/ElsaLab.Runner/Services/FileSystemDocumentStorageService.cs), and [InMemoryDocumentRepository.cs](../../src/ElsaLab.Runner/Services/InMemoryDocumentRepository.cs)
- Tests for movement, route journal, outputs, cancellation, idempotent re-execution, operation-key conflict, changed destination contents, and path-like input rejection: [EdmsDocumentStorageWorkflowTests.cs](../../tests/ElsaLab.Tests/EdmsDocumentStorageWorkflowTests.cs)
- Manual temporary-filesystem demonstration: [Program.cs](../../src/ElsaLab.Runner/Program.cs)
- Existing Elsa findings reused: [ELSA-03](../experiments/ELSA-03-custom-activity-di.md), [ELSA-04](../experiments/ELSA-04-flowchart-routing.md), and [activities and DI](../reference/activities-and-di.md).
