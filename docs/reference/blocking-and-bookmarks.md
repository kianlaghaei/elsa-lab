# Blocking Activities and Bookmarks

This reference combines bookmark creation observed in [ELSA-07](../experiments/ELSA-07-blocking-bookmark.md) with exact in-process resume behavior observed in [ELSA-08](../experiments/ELSA-08-external-resume.md). Neither experiment proves persistence across process shutdown.

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

## Exact resume and bookmark consumption

ELSA-08 used `IWorkflowResumer.ResumeAsync(bookmarkId, input, cancellationToken)` for application-level exact bookmark lookup. In the tested setup, the API found the bookmark through Elsa's `IBookmarkStore`, resumed its owning workflow instance through a runtime client, and returned the same workflow instance ID with `WorkflowStatus.Finished` and `WorkflowSubStatus.Finished`.

With the ELSA-07 bookmark's `AutoBurn=true`, successful resume removed the bookmark from the returned response's bookmark collection, the exported finished `WorkflowState`, and the in-memory bookmark store. A second exact-ID call returned `null`; it did not throw or run finalization a second time. The default runner state does not itself promise event de-duplication for external side effects.

The no-callback bookmark also had `AutoComplete=true`. The lower-level runner continuation showed the wait Activity become `Completed`, after which the Sequence scheduled `FinalizeDocument`. The wait context retained its ID, but the post-resume CLR `ActivityExecutionContext` was a new object. Its `ExecutionCount` was 1 again; that counter is not part of the serialized `ActivityExecutionContextState` shape.

Use the APIs according to what the caller has:

- `IWorkflowResumer` is the tested runtime API when application code has a bookmark ID and Elsa should locate and dispatch its owning instance.
- `IWorkflowRunner.RunAsync(workflowGraph, workflowState, options)` is the tested direct continuation path when the caller already holds the graph and state. It returns the execution journal needed to inspect Activity contexts.
- In this plain DI experiment, an ad-hoc graph run by `IWorkflowRunner` was not enough for the runtime client to resolve a versioned graph. Explicit `IWorkflowRegistry.RegisterAsync` of the code-first workflow enabled the `IWorkflowResumer` path. Other host startup/definition population paths were not tested.

`IWorkflowResumer` returned status, substatus, incidents, and remaining bookmarks, but its exact-ID overload did not include named workflow output in its response. The output was visible through `IWorkflowRuntime.CreateClientAsync(workflowInstanceId).ExportStateAsync()`. On a completed workflow, exported `WorkflowState.ActivityExecutionContexts` contained the completed root Workflow context (no parent, zero faults), but not the completed child Activity contexts. Elsa 3.8.4's `WorkflowExecutionContext.GetActiveActivityExecutionContexts` retains that root context so workflow-level state remains available. The lower-level runner's `Journal` was used to inspect the completed wait and finalization contexts.

In this registered code-first runtime test, a resume input dictionary was accepted by `IWorkflowResumer` but its values were null when consumed by downstream `GetInput<T>` expressions. The direct `IWorkflowRunner` continuation did consume those values. Treat this as a behavior of the specific tested path, and do not assume resume-time input transport works the same across runtime and direct-runner paths.

## Stimulus resume boundary

Stimulus resume was not tested. In tagged source, the generic `IWorkflowResumer` stimulus overload derives the bookmark name from the Activity type and hashes the stimulus. ELSA-07 instead assigned the custom name `DocumentReview` and included the Activity instance ID in the hash. Exact bookmark IDs were therefore the tested lookup method. See [ELSA-08](../experiments/ELSA-08-external-resume.md) for the source/runtime distinction and limits.

## Evidence and source

- Runtime evidence, test method names, exact states, and boundaries: [ELSA-07](../experiments/ELSA-07-blocking-bookmark.md).
- Executable Activity/workflow/tests: [Activity](../../src/ElsaLab.Runner/Activities/WaitForDocumentReviewActivity.cs), [workflow](../../src/ElsaLab.Runner/Workflows/DocumentReviewBlockingWorkflow.cs), and [tests](../../tests/ElsaLab.Tests/DocumentReviewBlockingWorkflowTests.cs).
- Elsa 3.8.4 tagged source: [`Activity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/Activity.cs), [`CodeActivity`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/CodeActivity.cs), [`Trigger`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Abstractions/Trigger.cs), [`ActivityExecutionContext`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`Bookmark`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/Bookmark.cs), [`CreateBookmarkArgs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/CreateBookmarkArgs.cs), and [`Sequence`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Activities/Sequence.cs).
- The built-in [`Event`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Activities/Event.cs) and [`WaitForEvent`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Extensions/ActivityExecutionContextEventExtensions.cs) show Elsa's event-trigger variant.
- ELSA-08 resume evidence and tagged sources: [experiment page](../experiments/ELSA-08-external-resume.md), [`IWorkflowResumer`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Contracts/IWorkflowResumer.cs), [`WorkflowResumer`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/WorkflowResumer.cs), [`LocalWorkflowClient`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/LocalWorkflowClient.cs), and [`DefaultActivityInvokerMiddleware`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Middleware/Activities/DefaultActivityInvokerMiddleware.cs).
