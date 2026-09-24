# Blocking Activities and Bookmarks

This reference summarizes the in-memory behavior tested in [ELSA-07](../experiments/ELSA-07-blocking-bookmark.md). It does not describe resume or persistence behavior.

## Creating the block

The tested custom wait inherits from Elsa's `Activity`, declares typed `Input<T>` ports, and calls `ActivityExecutionContext.CreateBookmark(CreateBookmarkArgs)`. It returns without calling `CompleteActivityAsync`. That leaves the Activity execution `Running`; a Sequence waits for child completion before scheduling its next child. In the test, the workflow returned with substatus `Suspended`, and the later `FinalizeDocument` activity had no execution context.

`CreateBookmark` does not require a callback for this blocking use. The tested bookmark had no `CallbackMethodName` and retained the default `AutoComplete=true`, but the experiment did not resume it and therefore does not establish how that setting behaves in a continuation.

Use `Activity` when the Activity must remain incomplete after creating the bookmark. The ELSA-07 Activity does not inherit from `CodeActivity`, which installs Elsa's `AutoCompleteBehavior` and is the one-shot shape used in ELSA-03. Elsa's `Trigger` base is for workflow trigger indexing; the built-in `Event` Activity uses `Trigger` plus `WaitForEvent`. ELSA-07 waits inside an already-started Sequence and uses a custom bookmark payload, so it creates that bookmark directly from a plain `Activity`.

## Bookmark identity and visibility

The 3.8.4 bookmark model carries a generated bookmark ID, name, hash, payload, Activity definition ID, graph node ID, Activity execution ID, creation timestamp, optional callback name, auto-burn/auto-complete flags, and optional metadata. In ELSA-07, the payload held document number, revision, and review key; the metadata repeated the review key for inspection.

The generated bookmark is visible in `WorkflowExecutionContext.Bookmarks` and the returned `WorkflowState.Bookmarks`. `ActivityExecutionContext.Bookmarks` filters by that execution context's ID. A bookmark did not appear as its own activity journal context, and the wait Activity's `JournalData` was empty; the bookmark list was the runtime structure containing the pending continuation.

When passing `CreateBookmarkArgs`, set `IncludeActivityInstanceId` explicitly if it should contribute to bookmark hashing. In Elsa 3.8.4, the argument object's Boolean defaults to `false`, while the no-argument API path has a `true` fallback. ELSA-07 explicitly set it to `true`.

The workflow instance ID belongs to the enclosing `WorkflowState`/`WorkflowExecutionContext`, not to the `Bookmark` object. The bookmark's `ActivityId`, `ActivityNodeId`, and `ActivityInstanceId` represent Activity definition, graph node, and Activity execution identity respectively. The test re-used one graph: Activity definition identity remained the same while two runs had distinct workflow IDs, execution IDs, bookmark IDs, and payloads.

## Runtime state and runner boundary

The tested `IWorkflowRunner` returned promptly with `WorkflowSubStatus.Suspended`; `WorkflowState.Status` was `WorkflowStatus.Running`. The workflow root, Sequence, and wait Activity contexts remained `Running`; the pre-wait Activity was `Completed`; no finalization context existed; and there were no faulted contexts.

`IWorkflowRunner` was sufficient to demonstrate and inspect the in-memory pause. No durable store was configured or queried. ELSA-07 does not establish whether a production host should find/resume waits through the runner, runtime APIs, a persisted bookmark store, or another host-specific integration.

## Evidence and source

- Runtime evidence, test method names, exact states, and boundaries: [ELSA-07](../experiments/ELSA-07-blocking-bookmark.md).
- Executable Activity/workflow/tests: [Activity](../../src/ElsaLab.Runner/Activities/WaitForDocumentReviewActivity.cs), [workflow](../../src/ElsaLab.Runner/Workflows/DocumentReviewBlockingWorkflow.cs), and [tests](../../tests/ElsaLab.Tests/DocumentReviewBlockingWorkflowTests.cs).
- Elsa 3.8.4 tagged source: [`Activity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/Activity.cs), [`CodeActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/CodeActivity.cs), [`Trigger`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/Trigger.cs), [`ActivityExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`Bookmark`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Bookmark.cs), [`CreateBookmarkArgs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/CreateBookmarkArgs.cs), and [`Sequence`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Sequence.cs).
- The built-in [`Event`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Activities/Event.cs) and [`WaitForEvent`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Extensions/ActivityExecutionContextEventExtensions.cs) show Elsa's event-trigger variant.
