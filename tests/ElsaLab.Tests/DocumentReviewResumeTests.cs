using Elsa.Workflows;
using Elsa.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public class DocumentReviewResumeTests
{
    private const string DocumentNumber = "DPC-10-ME-0001";
    private const int Revision = 2;

    [Fact]
    public async Task Resumer_ContinuesExactBookmark_AutoCompletesAndConsumesIt_AndIgnoresDuplicateResume()
    {
        using var provider = CreateServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var services = scope.ServiceProvider;
        var workflowGraph = await BuildAndRegisterWorkflowAsync(services);
        var runner = services.GetRequiredService<IWorkflowRunner>();
        var resumer = services.GetRequiredService<IWorkflowResumer>();
        var bookmarkStore = services.GetRequiredService<IBookmarkStore>();
        var runtime = services.GetRequiredService<IWorkflowRuntime>();

        var started = await runner.RunAsync(workflowGraph, CreateStartOptions(DocumentNumber, Revision));
        AssertSuspended(started);

        var waitBeforeResume = Assert.Single(
            started.Journal.ActivityExecutionContexts,
            context => context.Activity is WaitForDocumentReviewActivity);
        var bookmark = Assert.Single(started.WorkflowState.Bookmarks);
        Assert.Equal(waitBeforeResume.Id, bookmark.ActivityInstanceId);
        Assert.Null(bookmark.CallbackMethodName);
        Assert.True(bookmark.AutoComplete);
        Assert.True(bookmark.AutoBurn);
        var storedBookmark = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmark.Id });
        Assert.NotNull(storedBookmark);
        Assert.Equal(bookmark.Id, storedBookmark.Id);
        Assert.Equal(started.WorkflowState.Id, storedBookmark.WorkflowInstanceId);
        Assert.Equal(waitBeforeResume.Id, storedBookmark.ActivityInstanceId);

        var resumeInput = CreateResumeInput("Approved", "reviewer-123");
        var response = await resumer.ResumeAsync(bookmark.Id, resumeInput, CancellationToken.None);

        Assert.NotNull(response);
        Assert.Equal(started.WorkflowState.Id, response.WorkflowInstanceId);
        Assert.Equal(WorkflowStatus.Finished, response.Status);
        Assert.Equal(WorkflowSubStatus.Finished, response.SubStatus);
        Assert.Empty(response.Bookmarks);
        Assert.Null(response.Output);
        Assert.Empty(response.Incidents);
        Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmark.Id }));

        var client = await runtime.CreateClientAsync(started.WorkflowState.Id);
        var completedState = await client.ExportStateAsync();
        Assert.Equal(WorkflowStatus.Finished, completedState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, completedState.SubStatus);
        Assert.Empty(completedState.Bookmarks);
        var completedRootContext = Assert.Single(completedState.ActivityExecutionContexts);
        Assert.Null(completedRootContext.ParentContextId);
        Assert.Equal(ActivityStatus.Completed, completedRootContext.Status);
        Assert.Equal(0, completedRootContext.FaultCount);
        Assert.Empty(completedState.Incidents);
        Assert.Equal("ReviewCompleted", completedState.Output["FinalStatus"]);
        Assert.Equal(true, completedState.Output["Finalized"]);
        Assert.Null(completedState.Output["ReviewOutcome"]);
        Assert.Null(completedState.Output["Reviewer"]);

        var duplicateResponse = await resumer.ResumeAsync(bookmark.Id, resumeInput, CancellationToken.None);
        Assert.Null(duplicateResponse);

        var stateAfterDuplicate = await client.ExportStateAsync();
        Assert.Equal(WorkflowStatus.Finished, stateAfterDuplicate.Status);
        Assert.Equal("ReviewCompleted", stateAfterDuplicate.Output["FinalStatus"]);
        Assert.Equal(true, stateAfterDuplicate.Output["Finalized"]);
        Assert.Null(stateAfterDuplicate.Output["ReviewOutcome"]);
        Assert.Empty(stateAfterDuplicate.Bookmarks);
    }

    [Fact]
    public async Task RunnerResume_CompletesTheExistingWaitContext_AndSchedulesFinalizationOnce()
    {
        using var provider = CreateServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var services = scope.ServiceProvider;
        var runner = services.GetRequiredService<IWorkflowRunner>();
        var graph = await BuildAndRegisterWorkflowAsync(services);

        var started = await runner.RunAsync(graph, CreateStartOptions(DocumentNumber, Revision));
        AssertSuspended(started);

        var waitBeforeResume = Assert.Single(
            started.Journal.ActivityExecutionContexts,
            context => context.Activity is WaitForDocumentReviewActivity);
        Assert.Equal(1L, waitBeforeResume.ExecutionCount);
        var bookmark = Assert.Single(started.WorkflowState.Bookmarks);

        var resumed = await runner.RunAsync(
            graph,
            started.WorkflowState,
            new RunWorkflowOptions
            {
                BookmarkId = bookmark.Id,
                Input = CreateResumeInput("Approved", "reviewer-123")
            });

        Assert.Equal(WorkflowStatus.Finished, resumed.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, resumed.WorkflowExecutionContext.SubStatus);
        Assert.Empty(resumed.WorkflowState.Bookmarks);

        var waitAfterResume = Assert.Single(
            resumed.Journal.ActivityExecutionContexts,
            context => context.Activity is WaitForDocumentReviewActivity);
        Assert.Equal(ActivityStatus.Completed, waitAfterResume.Status);
        Assert.Equal(waitBeforeResume.Id, waitAfterResume.Id);
        Assert.Equal(1L, waitAfterResume.ExecutionCount);
        Assert.NotSame(waitBeforeResume, waitAfterResume);
        Assert.Same(waitBeforeResume.Activity, waitAfterResume.Activity);
        Assert.Empty(waitAfterResume.JournalData);

        var finalization = Assert.Single(
            resumed.Journal.ActivityExecutionContexts,
            context => context.Activity.Name == "FinalizeDocument");
        Assert.Equal(ActivityStatus.Completed, finalization.Status);
        Assert.Equal(waitAfterResume.Id, finalization.SchedulingActivityExecutionId);
        Assert.Single(resumed.Journal.ActivityExecutionContexts, context => context.Activity.Name == "RecordReviewCompletion");
        Assert.DoesNotContain(resumed.Journal.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Equal("ReviewCompleted", resumed.WorkflowState.Output["FinalStatus"]);
        Assert.Equal(true, resumed.WorkflowState.Output["Finalized"]);
        Assert.Equal("Approved", resumed.WorkflowState.Output["ReviewOutcome"]);
        Assert.Equal("reviewer-123", resumed.WorkflowState.Output["Reviewer"]);
    }

    [Fact]
    public async Task ResumingOneOfTwoBookmarks_LeavesTheOtherWorkflowSuspendedAndIsolated()
    {
        using var provider = CreateServices().BuildServiceProvider();
        using var scope = provider.CreateScope();

        var services = scope.ServiceProvider;
        var runner = services.GetRequiredService<IWorkflowRunner>();
        var graph = await BuildAndRegisterWorkflowAsync(services);
        var resumer = services.GetRequiredService<IWorkflowResumer>();
        var bookmarkStore = services.GetRequiredService<IBookmarkStore>();
        var runtime = services.GetRequiredService<IWorkflowRuntime>();

        var documentA = await runner.RunAsync(graph, CreateStartOptions("DPC-10-ME-0001", Revision, "review-A"));
        var documentB = await runner.RunAsync(graph, CreateStartOptions("DPC-10-ME-0002", revision: 5, "review-B"));
        AssertSuspended(documentA);
        AssertSuspended(documentB);

        var bookmarkA = Assert.Single(documentA.WorkflowState.Bookmarks);
        var bookmarkB = Assert.Single(documentB.WorkflowState.Bookmarks);
        Assert.NotEqual(bookmarkA.Id, bookmarkB.Id);
        Assert.NotEqual(documentA.WorkflowState.Id, documentB.WorkflowState.Id);
        Assert.Equal("DPC-10-ME-0001", Assert.IsType<DocumentReviewBookmarkPayload>(bookmarkA.Payload).DocumentNumber);
        Assert.Equal("DPC-10-ME-0002", Assert.IsType<DocumentReviewBookmarkPayload>(bookmarkB.Payload).DocumentNumber);
        var storedBookmarkA = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkA.Id });
        var storedBookmarkB = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkB.Id });
        Assert.NotNull(storedBookmarkA);
        Assert.NotNull(storedBookmarkB);
        Assert.Equal(documentA.WorkflowState.Id, storedBookmarkA.WorkflowInstanceId);
        Assert.Equal(documentB.WorkflowState.Id, storedBookmarkB.WorkflowInstanceId);

        var wrongBookmark = await resumer.ResumeAsync("bookmark-that-does-not-exist", CreateResumeInput("Approved", "reviewer-A"), CancellationToken.None);
        Assert.Null(wrongBookmark);

        var responseA = await resumer.ResumeAsync(bookmarkA.Id, CreateResumeInput("Approved", "reviewer-A"), CancellationToken.None);
        Assert.NotNull(responseA);
        Assert.Equal(documentA.WorkflowState.Id, responseA.WorkflowInstanceId);
        Assert.Equal(WorkflowStatus.Finished, responseA.Status);
        Assert.Equal(WorkflowSubStatus.Finished, responseA.SubStatus);
        Assert.Empty(responseA.Bookmarks);
        Assert.Empty(responseA.Incidents);
        Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkA.Id }));

        var clientA = await runtime.CreateClientAsync(documentA.WorkflowState.Id);
        var clientB = await runtime.CreateClientAsync(documentB.WorkflowState.Id);
        var stateA = await clientA.ExportStateAsync();
        var stateB = await clientB.ExportStateAsync();

        Assert.Equal("ReviewCompleted", stateA.Output["FinalStatus"]);
        Assert.Equal(true, stateA.Output["Finalized"]);
        Assert.Equal(WorkflowStatus.Running, stateB.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, stateB.SubStatus);
        Assert.Empty(stateB.Output);
        var activeWaitB = Assert.Single(stateB.ActivityExecutionContexts, context => context.Id == bookmarkB.ActivityInstanceId);
        Assert.Equal(ActivityStatus.Running, activeWaitB.Status);
        Assert.Contains(stateB.Bookmarks, bookmark => bookmark.Id == bookmarkB.Id);
        Assert.NotNull(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkB.Id }));

        var responseB = await resumer.ResumeAsync(bookmarkB.Id, CreateResumeInput("NeedsRevision", "reviewer-B"), CancellationToken.None);
        Assert.NotNull(responseB);
        Assert.Equal(documentB.WorkflowState.Id, responseB.WorkflowInstanceId);
        Assert.Equal(WorkflowStatus.Finished, responseB.Status);
        Assert.Equal(WorkflowSubStatus.Finished, responseB.SubStatus);
        Assert.Empty(responseB.Bookmarks);
        Assert.Empty(responseB.Incidents);

        stateB = await clientB.ExportStateAsync();
        Assert.Equal("ReviewCompleted", stateB.Output["FinalStatus"]);
        Assert.Equal(true, stateB.Output["Finalized"]);
        Assert.Empty(stateB.Bookmarks);
        Assert.Empty(stateB.Incidents);
        Assert.DoesNotContain(stateB.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
    }

    private static void AssertSuspended(RunWorkflowResult result)
    {
        Assert.Equal(WorkflowStatus.Running, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, result.WorkflowExecutionContext.SubStatus);

        var contexts = result.Journal.ActivityExecutionContexts;
        Assert.DoesNotContain(contexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Single(contexts, context => context.Activity.Name == "PrepareDocumentReview");
        Assert.Single(contexts, context => context.Activity is WaitForDocumentReviewActivity && context.Status == ActivityStatus.Running);
        Assert.DoesNotContain(contexts, context => context.Activity.Name == "FinalizeDocument");
        Assert.Single(result.WorkflowState.Bookmarks);
    }

    private static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa =>
        {
            elsa.AddActivity<WaitForDocumentReviewActivity>();
            elsa.AddWorkflow<DocumentReviewBlockingWorkflow>();
        });

        return services;
    }

    private static async Task<WorkflowGraph> BuildAndRegisterWorkflowAsync(IServiceProvider services)
    {
        var builder = services.GetRequiredService<IWorkflowBuilderFactory>().CreateBuilder();
        var workflow = await builder.BuildWorkflowAsync<DocumentReviewBlockingWorkflow>();
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        return await services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
    }

    private static RunWorkflowOptions CreateStartOptions(string documentNumber, int revision, string reviewKey = "discipline-review") => new()
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = documentNumber,
            ["Revision"] = revision,
            ["ReviewKey"] = reviewKey,
            ["ReviewOutcome"] = "Pending",
            ["Reviewer"] = "Unassigned"
        }
    };

    private static IDictionary<string, object> CreateResumeInput(string outcome, string reviewer) => new Dictionary<string, object>
    {
        ["ReviewOutcome"] = outcome,
        ["Reviewer"] = reviewer
    };
}
