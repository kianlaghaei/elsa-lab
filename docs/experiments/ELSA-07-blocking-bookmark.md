# ELSA-07 — Blocking Activity / Bookmark

| Metadata | Value |
|---|---|
| Experiment | ELSA-07 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | Pending feature commit |

## Observed

`DocumentReviewBlockingWorkflow` is a linear Elsa `Sequence` with `PrepareDocumentReview`, `WaitForDocumentReviewActivity`, and `FinalizeDocument`. The caller supplied `DocumentNumber`, `Revision`, and `ReviewKey` through `RunWorkflowOptions.Input`. Elsa resolved those values into the Activity's typed `Input<T>` ports; the wait Activity used the resolved values to construct its bookmark payload.

The first run returned from `IWorkflowRunner` without hanging. The exact returned state was:

| Item | Observed value |
|---|---|
| `WorkflowState.Status` | `WorkflowStatus.Running` |
| `WorkflowExecutionContext.SubStatus` | `WorkflowSubStatus.Suspended` |
| Workflow root execution context | `ActivityStatus.Running` |
| `DocumentReviewSequence` | `ActivityStatus.Running` |
| `PrepareDocumentReview` | `ActivityStatus.Completed` |
| `WaitForDocumentReview` | `ActivityStatus.Running`, execution count 1 |
| `FinalizeDocument` | No execution context; it did not run |
| Faulted activity contexts | None |

The workflow is therefore suspended, not successfully finished. In this Elsa version, the public top-level state remains `Running` while its substatus is `Suspended`.

No Activity output, named workflow output, or typed workflow result was used: the workflow intentionally has not completed. The test's evidence is the suspended state, unfinished wait context, execution journal, and bookmark data.

The wait execution's `SchedulingActivityExecutionId` matched the `PrepareDocumentReview` execution ID. The root and Sequence remained running; the wait execution was not marked completed. The wait context had empty `JournalData`. The activity journal contained a context for the wait, but bookmark creation did not create another activity execution context or a bookmark-specific journal-data entry. The bookmark was visible through `WorkflowExecutionContext.Bookmarks`, `WorkflowState.Bookmarks`, and the wait context's `Bookmarks` property. The same bookmark ID appeared in the returned workflow context and state.

The bookmark observed in the sample run had `Name=DocumentReview`, a generated `Id`, a non-empty `Hash`, and `DocumentReviewBookmarkPayload(DocumentNumber="DPC-10-ME-0001", Revision=2, ReviewKey="discipline-review")`. Its `Metadata` contained the review key. It also carried Elsa's `ActivityId`, `ActivityNodeId`, `ActivityInstanceId`, and `CreatedAt`. The test confirmed that `ActivityId` and `ActivityNodeId` identify the Activity definition/graph node, while `ActivityInstanceId` matched the wait execution context ID. `WorkflowState.Id` matched the workflow execution context ID that owns the bookmark collection.

The test built one `WorkflowGraph` and ran it twice with different document/revision inputs. Elsa reused the same wait Activity object and Activity ID/Node ID, but each run had a different workflow state ID, activity execution context ID, bookmark ID, bookmark hash, input, and payload. Each returned state contained only its own bookmark. The Activity stores no per-run mutable fields.

## Confirmed from Elsa 3.8.4 source

**Source-confirmed:** the custom blocking Activity inherits from Elsa's `Activity`, not `CodeActivity`. `Activity` does not install the auto-complete behavior; completion is an explicit `ActivityExecutionContext.CompleteActivityAsync()` operation. The implementation creates the bookmark and deliberately does not call that completion method. Elsa's `CodeActivity` installs `AutoCompleteBehavior`, the one-shot shape used for normal activities such as the ELSA-03 adapter, so it is not the right base for this wait.

**Source-confirmed:** `ActivityExecutionContext.CreateBookmark(CreateBookmarkArgs)` accepts a bookmark name, stimulus/payload, optional callback, metadata, auto-burn and auto-complete settings, and whether the activity instance ID participates in the stimulus hash. Its source documentation says creating a bookmark suspends the workflow after pending activities have executed. The callback is optional; no callback was supplied here. The resulting bookmark reported `CallbackMethodName=null`, `AutoBurn=true`, and `AutoComplete=true` (the last two are creation defaults). Their effects on a resumed workflow were not tested.

**Source-confirmed:** a bookmark includes a generated bookmark ID, name, hash, payload, Activity ID, Activity node ID, Activity instance ID, creation time, callback method name, auto-burn/auto-complete flags, and optional metadata. `CreateBookmark` adds it to the current `WorkflowExecutionContext.Bookmarks`; `ActivityExecutionContext.Bookmarks` selects bookmarks whose `ActivityInstanceId` matches that execution context's ID. Elsa hashes the name and payload and can include the Activity instance ID.

**Source-confirmed and runtime-observed:** `CreateBookmarkArgs.IncludeActivityInstanceId` defaults to `false`, so this Activity explicitly sets it to `true`. The no-argument `CreateBookmark()` code path has a `true` fallback, but supplying an options object uses that object's Boolean value. Setting the flag explicitly avoids depending on the difference between those paths.

**Source-confirmed:** Elsa's `Sequence` schedules its next child from the previous child's completion callback. Since the wait Activity did not complete, `FinalizeDocument` was not scheduled. Elsa's workflow status mapping maps `WorkflowSubStatus.Suspended` to top-level `WorkflowStatus.Running`.

**Source-confirmed:** Elsa has a `Trigger` base for Activities that participate in workflow trigger indexing. Its built-in `Event` Activity inherits from `Trigger<object?>` and uses `WaitForEvent`, which creates an event-named bookmark. This workflow waits inside an already-running Sequence and only needs a document/revision bookmark payload, so the experiment uses plain `Activity` and calls `CreateBookmark` directly; it does not configure trigger indexing.

**Source-confirmed:** Elsa's tagged blocking integration test also starts a Sequence through `IWorkflowRunner`, observes a bookmark in the returned `WorkflowState`, and verifies that the later `WriteLine` steps have not run. The lab used the same real in-process runner pattern. In this no-persistence setup, `IWorkflowRunner` was sufficient to start the workflow and return its suspended state and bookmark. We did not query a durable bookmark/instance store. The `IWorkflowRunner` overload that accepts a `WorkflowState` was not used to resume anything in this experiment.

## Inference / EDMS implication

**Inference:** a blocking Activity can represent the point after assigning a review or sending a document where workflow progress waits for a later event. A compact payload such as document number, revision, and review key can make the pending continuation intelligible. Production correlation, authorization, inboxes, delivery, and durable state require separate experiments and application design.

**Inference:** the same shape may fit “assign discipline review, then wait” or “send to vendor, then wait for a revision.” This experiment proves only that Elsa created an in-memory bookmark and returned a suspended execution. It does not prove that an API can find or trigger it.

## Limitations

- No bookmark was triggered and no resume API was called. The callback and `AutoComplete` behavior on resume remain untested.
- No SQL/EF/database persistence, durable bookmark store, process restart, multi-server host, or recovery path was configured or tested. Returned in-memory state is not evidence of durability.
- No human-task inbox or external reviewer/vendor event was implemented.
- The workflow ran through the in-process `IWorkflowRunner`; the experiment does not determine which APIs a production host should use for durable lookup or resume.
- The payload is a small in-memory record. Serialization and compatibility across a process restart were not tested.
- Cancellation, bookmark deletion, expiration, and multiple bookmarks on one Activity were not tested.

## Executable evidence

- Blocking Activity and payload: [WaitForDocumentReviewActivity.cs](../../src/ElsaLab.Runner/Activities/WaitForDocumentReviewActivity.cs)
- Sequence and input bindings: [DocumentReviewBlockingWorkflow.cs](../../src/ElsaLab.Runner/Workflows/DocumentReviewBlockingWorkflow.cs)
- Manual blocked-state demonstration: [Program.cs](../../src/ElsaLab.Runner/Program.cs)
- Tests: [DocumentReviewBlockingWorkflowTests.cs](../../tests/ElsaLab.Tests/DocumentReviewBlockingWorkflowTests.cs), methods `WorkflowBlocksAtBookmark_AndDoesNotFinalizeDocument`, `BookmarkCarriesDocumentReviewAndElsaExecutionIdentity`, and `IndependentWorkflowRunsKeepBookmarkAndExecutionStateSeparate`.

## Elsa source references

- Tagged [`Activity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/Activity.cs), [`CodeActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/CodeActivity.cs), [`Trigger`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/Trigger.cs), and [`Sequence`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Sequence.cs).
- Tagged [`ActivityExecutionContext` bookmark API](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`CreateBookmarkArgs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/CreateBookmarkArgs.cs), [`Bookmark`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Bookmark.cs), and [`WorkflowExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/WorkflowExecutionContext.cs).
- Tagged [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs), [`IWorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowRunner.cs), [`WorkflowStateExtractor`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowStateExtractor.cs), and [`WorkflowExecutionLogEntry`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/WorkflowExecutionLogEntry.cs).
- Tagged built-in [`Event`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Activities/Event.cs) and [`WaitForEvent`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Extensions/ActivityExecutionContextEventExtensions.cs) sources illustrate the trigger/event-specific option.
- Elsa 3.8.4 tagged blocking [tests](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/Blocking/Tests.cs) and [workflow](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/Blocking/Workflows.cs) use `IWorkflowRunner` and check that downstream Sequence activities are not run before bookmark continuation.
