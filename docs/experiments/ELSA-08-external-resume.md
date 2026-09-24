# ELSA-08 — External Event + Suspend / Resume

| Metadata | Value |
|---|---|
| Experiment | ELSA-08 |
| Status | VERIFIED |
| Elsa | 3.8.4 |
| .NET | 10 (`net10.0`) |
| Verified date | 2026-09-24 |
| Implementation commit | [`164256325697f1ca5b9db2e220cf4016715bbd53`](https://github.com/kianlaghaei/elsa-lab/commit/164256325697f1ca5b9db2e220cf4016715bbd53) |

## Observed

The existing `DocumentReviewBlockingWorkflow` remains a linear Sequence: `PrepareDocumentReview` → `WaitForDocumentReviewActivity` → `FinalizeDocument`. The wait Activity from ELSA-07 was left unchanged: it creates the `DocumentReview` bookmark with the typed `DocumentReviewBookmarkPayload`, `IncludeActivityInstanceId=true`, no callback, and the default `AutoBurn=true` and `AutoComplete=true` flags.

The workflow was built as a code-first graph, registered through Elsa's `IWorkflowRegistry`, and started through `IWorkflowRunner`. Before continuation the actual state was:

| Item | Observed value |
|---|---|
| `WorkflowState.Id` | Workflow instance identity |
| `WorkflowState.Status` | `WorkflowStatus.Running` |
| `WorkflowExecutionContext.SubStatus` | `WorkflowSubStatus.Suspended` |
| Wait Activity | `ActivityStatus.Running` |
| Bookmark | Present in `WorkflowState.Bookmarks` and Elsa's `IBookmarkStore` |
| Bookmark `Id` | Generated bookmark identity |
| Bookmark `Hash` | Non-empty stimulus hash |
| Bookmark `ActivityInstanceId` | Equal to the wait execution context ID |
| Stored bookmark `WorkflowInstanceId` | Equal to `WorkflowState.Id` |
| `FinalizeDocument` | Not yet executed |

Application code then called `IWorkflowResumer.ResumeAsync(bookmark.Id, input, cancellationToken)`. The response was `WorkflowStatus.Finished` / `WorkflowSubStatus.Finished`; it identified the same workflow instance, had no incidents, and contained no remaining bookmarks. A query to `IBookmarkStore` also found no bookmark with that ID. The default `RunWorkflowInstanceResponse.Output` was null for this call. The final named workflow outputs were visible by exporting the state through `IWorkflowRuntime.CreateClientAsync(workflowInstanceId).ExportStateAsync()`; they were `FinalStatus="ReviewCompleted"` and `Finalized=true`.

After successful resume, a second `IWorkflowResumer.ResumeAsync` using the burned bookmark returned `null`. The exported workflow state remained finished with the same outputs and no bookmarks.

The two-instance test started separate document waits. Resuming document A finished only A. Document B remained `Running` / `Suspended`, retained its own bookmark in state and the store, and had no final outputs until it was separately resumed. Each stored bookmark's `WorkflowInstanceId` matched its owning workflow state ID.

### Resume input observation

The `IWorkflowResumer` overload accepts a name-keyed input dictionary. In this registered code-first workflow path, the test supplied `ReviewOutcome="Approved"` and `Reviewer="reviewer-123"`, but the downstream `GetInput<T>` expressions produced null named outputs. The runtime response completed successfully, but that did not prove that these values were available as workflow inputs.

The separate `IWorkflowRunner.RunAsync(graph, workflowState, new RunWorkflowOptions { BookmarkId, Input })` continuation did make those same values available. Its final `WorkflowState.Output` contained the expected `ReviewOutcome` and `Reviewer`. This difference is specific to the tested paths and is not generalized to every Elsa host or materializer. Treat resume-time data transport through `IWorkflowResumer` as unresolved for this code-first setup.

## Confirmed from Elsa 3.8.4 source

- `IWorkflowResumer` has an exact bookmark-ID overload and stimulus/filter overloads. The exact overload searches `IBookmarkStore`; when it finds a bookmark, `WorkflowResumer` creates a client for the stored `WorkflowInstanceId` and runs that instance with the bookmark ID and supplied options.
- The runtime client resolves the workflow graph using the workflow instance's definition version and invokes `IWorkflowRunner` with `RunWorkflowOptions.BookmarkId`. In this version, older `IWorkflowRuntime.ResumeWorkflowAsync` methods are marked obsolete in favor of creating a client and using its API.
- The exact overload's `RunWorkflowInstanceResponse` exposes instance ID, status, substatus, bookmarks, incidents, and optional output. `LocalWorkflowClient` only includes output when `IncludeWorkflowOutput` is requested. The `IWorkflowResumer.ResumeAsync(bookmarkId, input, token)` implementation does not set that flag; the test therefore inspected output through exported `WorkflowState` instead.
- `ActivityExecutionContext.CreateBookmark(CreateBookmarkArgs)` defaults `AutoBurn` and `AutoComplete` to true and stores the callback method name when a callback is supplied. The middleware removes a resumed bookmark with `AutoBurn=true` after activity invocation.
- Elsa's `WorkflowState` carries named outputs and bookmarks. After completion, this test's exported `ActivityExecutionContexts` contained the completed root Workflow context, with no parent and zero faults; it did not contain the completed child Activity journal. Tagged `WorkflowExecutionContext.GetActiveActivityExecutionContexts` keeps the root context even when completed so workflow-level state remains available. The 3.8.4 `ActivityExecutionContextState` does not serialize `ExecutionCount`; the state extractor creates new runtime context objects and reapplies their IDs and statuses when reconstructing state.
- Elsa's tagged blocking integration test demonstrates the lower-level runner continuation: it gets a bookmark from `WorkflowState`, then calls `IWorkflowRunner.RunAsync(workflow, workflowState, new RunWorkflowOptions { BookmarkId = ... })`.

## Exact bookmark resume

**Observed:** exact ID resume selected the intended workflow. Before resume, the test captured the workflow state ID, bookmark ID and hash, Activity instance ID, typed document/revision/review-key payload, and owning bookmark-store workflow ID. `IWorkflowResumer` returned the same workflow instance ID after continuation.

**Source-confirmed:** `IWorkflowResumer` is the higher-level application API for looking up a stored bookmark by ID and dispatching it to the owning runtime client. `IWorkflowRunner` can also continue a caller-held `WorkflowState` directly, but it does not itself look up an arbitrary bookmark ID in Elsa's bookmark store.

In this test's bare DI setup, starting an ad-hoc graph through `IWorkflowRunner` alone did not make the graph retrievable by the runtime client. Registering the built code-first workflow with Elsa's native `IWorkflowRegistry.RegisterAsync` made the definition available for `IWorkflowResumer` to resolve. A host that runs Elsa startup/definition population may arrange this differently; that host path was not tested here.

## AutoBurn behavior

**Observed:** before resume, the bookmark existed in `WorkflowState.Bookmarks` and `IBookmarkStore`. After successful resume, `RunWorkflowInstanceResponse.Bookmarks` was empty, the exported finished state had no bookmarks, and `IBookmarkStore.FindAsync` returned null for the exact ID.

**Observed:** a duplicate exact-ID resume returned null rather than throwing. No finalization was scheduled again; the lower-level continuation journal contained one `FinalizeDocument` context, and the runtime path no longer had a matching bookmark to dispatch.

## AutoComplete behavior

The wait bookmark had `CallbackMethodName=null` and `AutoComplete=true`. On the lower-level native continuation test, the wait Activity's status changed from `Running` to `Completed`, then Elsa scheduled the downstream `FinalizeDocument` Sequence and the workflow finished. This is runtime evidence that the tested no-callback bookmark auto-completed the waiting Activity when resumed.

The pre-resume and post-resume wait execution contexts had the same ID and referenced the same Activity definition object, but they were different `ActivityExecutionContext` objects. The observed `ExecutionCount` was 1 before and 1 after reconstruction/resume; it did not accumulate to 2. The state shape lacks an execution-count field, so this count should not be treated as a cross-resume audit counter.

## Duplicate resume behavior

The first runtime resumption finished the workflow and burned the bookmark. A second call to `IWorkflowResumer.ResumeAsync` with the same bookmark ID returned `null`. The final state and outputs were unchanged. This is the observed behavior of the default in-memory store and resumer path; it is not a general event-deduplication guarantee for arbitrary external messages or side effects.

## Workflow isolation

Two workflow instances had different state IDs, bookmark IDs, Activity instance IDs, payloads, and stored bookmark owner IDs. Resuming A did not alter B's `Running` / `Suspended` status or consume B's bookmark. B was then resumed independently. No workflow data crossed between the two test instances.

## Stimulus/correlation behavior

Stimulus-based resume was not executed. Source inspection shows that `IWorkflowResumer.ResumeAsync<TActivity>(stimulus)` derives a bookmark name from the Activity type and computes a stimulus hash. ELSA-07 explicitly names this bookmark `DocumentReview` and sets `IncludeActivityInstanceId=true`, so its hash includes the activity execution ID. The generic stimulus overload therefore does not match this bookmark shape without aligning its name/hash inputs; this experiment selected exact bookmark IDs instead.

The bookmark itself does not contain the owning workflow instance ID. Elsa's `StoredBookmark` does carry `WorkflowInstanceId` and `ActivityInstanceId`, and the test verified those associations in the default store.

## Runner vs Runtime conclusion

**Observed and source-confirmed:** use `IWorkflowResumer` for application code that needs Elsa to locate a stored bookmark by its exact ID and dispatch the owning instance through Elsa's runtime client. The runtime path requires the workflow instance and its versioned definition to be resolvable. Use `IWorkflowRunner` when the caller already holds the workflow graph and `WorkflowState` and wants the direct in-process continuation pattern; this path also returns the journal used to inspect Activity execution contexts.

The test used Elsa's default in-memory runtime stores. It did not prove that a bookmark survives process exit, is available on another server, or can be resumed after restart.

## Inference / EDMS implication

An EDMS application can store the Elsa `WorkflowInstanceId` and `BookmarkId` beside a review task, then use the exact bookmark ID after validating the task's current state. For example:

```text
ReviewTask
├── WorkflowInstanceId
├── BookmarkId
├── DocumentRevisionId
├── Discipline
├── Assignee
└── Status
```

After an authenticated reviewer submits a result, application code can validate ownership and authorization, then call Elsa's resumer. The bookmark identifies a continuation target; it does not define who is allowed to trigger it. Resume-time outcome data should not be assumed to enter `WithInput<T>` workflow inputs on the tested `IWorkflowResumer` path without an additional focused verification.

## Security implication

Do not expose raw bookmark IDs as authorization tokens or allow arbitrary callers to invoke bookmark resume. A future EDMS endpoint should authenticate the caller, validate task ownership/permissions and current task state, then select the stored bookmark ID internally. This experiment implements no endpoint or authorization system.

## Limitations

- Only Elsa 3.8.4's in-process, default in-memory bookmark and workflow-instance stores were exercised. No SQL persistence, process restart, multi-server runtime, or durable recovery was tested.
- No HTTP/webhook/message-broker event source, reviewer inbox, task ownership model, authorization, or external resume endpoint was implemented.
- The optional stimulus-based path and an `IncludeActivityInstanceId=false` variation were not run.
- `IWorkflowResumer` resume input values were not observable through downstream workflow input expressions in this test's registered code-first path. The lower-level `IWorkflowRunner` path did consume them; the difference needs investigation before choosing a production resume-result contract.
- Runtime-client responses do not include the activity journal used by the lower-level runner test. The runtime test verified final status, bookmarks, incidents, and exported named outputs instead.
- Bookmark delivery races, concurrent duplicate requests, callback behavior, cancellation during resume, and cleanup/expiration were not tested.

## Executable evidence

- Existing blocking Activity and typed payload: [WaitForDocumentReviewActivity.cs](../../src/ElsaLab.Runner/Activities/WaitForDocumentReviewActivity.cs).
- Extended code-first Sequence, workflow outputs, and finalization: [DocumentReviewBlockingWorkflow.cs](../../src/ElsaLab.Runner/Workflows/DocumentReviewBlockingWorkflow.cs).
- Manual in-process resume demonstration: [Program.cs](../../src/ElsaLab.Runner/Program.cs).
- Runtime, duplicate, bookmark consumption, and two-instance isolation tests: [DocumentReviewResumeTests.cs](../../tests/ElsaLab.Tests/DocumentReviewResumeTests.cs), methods `Resumer_ContinuesExactBookmark_AutoCompletesAndConsumesIt_AndIgnoresDuplicateResume`, `RunnerResume_CompletesTheExistingWaitContext_AndSchedulesFinalizationOnce`, and `ResumingOneOfTwoBookmarks_LeavesTheOtherWorkflowSuspendedAndIsolated`.
- ELSA-07 bookmark creation baseline: [DocumentReviewBlockingWorkflowTests.cs](../../tests/ElsaLab.Tests/DocumentReviewBlockingWorkflowTests.cs).

## Elsa source references

- Tagged [`IWorkflowResumer`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Contracts/IWorkflowResumer.cs), [`WorkflowResumer`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/WorkflowResumer.cs), [`IWorkflowRuntime`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Contracts/IWorkflowRuntime.cs), [`IWorkflowClient`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Contracts/IWorkflowClient.cs), and [`LocalWorkflowClient`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Services/LocalWorkflowClient.cs).
- Tagged [`IWorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contracts/IWorkflowRunner.cs), [`WorkflowRunner`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowRunner.cs), and [blocking integration tests](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/test/integration/Elsa.Workflows.IntegrationTests/Scenarios/Blocking/Tests.cs).
- Tagged [`ActivityExecutionContext.CreateBookmark`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/ActivityExecutionContext.cs), [`DefaultActivityInvokerMiddleware`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Middleware/Activities/DefaultActivityInvokerMiddleware.cs), and [`CreateBookmarkArgs`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Models/CreateBookmarkArgs.cs).
- Tagged [`IBookmarkStore`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Contracts/IBookmarkStore.cs), [`StoredBookmark`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Entities/StoredBookmark.cs), [`BookmarkFilter`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Filters/BookmarkFilter.cs), [`RunWorkflowInstanceResponse`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Messages/RunWorkflowInstanceResponse.cs), and [`ResumeBookmarkOptions`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Options/ResumeBookmarkOptions.cs).
- Tagged [`WorkflowState`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/State/WorkflowState.cs), [`ActivityExecutionContextState`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/State/ActivityExecutionContextState.cs), and [`WorkflowStateExtractor`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Services/WorkflowStateExtractor.cs).
- Tagged [`WorkflowExecutionContext.GetActiveActivityExecutionContexts`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Core/Contexts/WorkflowExecutionContext.cs), which retains the root Workflow context after completion.
- Tagged [default runtime feature and in-memory store registration](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Features/WorkflowRuntimeFeature.cs), [`MemoryBookmarkStore`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Runtime/Stores/MemoryBookmarkStore.cs), and [`MemoryWorkflowInstanceStore`](https://github.com/elsa-workflows/elsa-core/blob/3.8.4/src/modules/Elsa.Workflows.Management/Stores/MemoryWorkflowInstanceStore.cs).
