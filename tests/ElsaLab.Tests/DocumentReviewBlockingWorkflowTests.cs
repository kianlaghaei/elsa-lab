using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public class DocumentReviewBlockingWorkflowTests
{
    private const string ReviewKey = "discipline-review";

    [Fact]
    public async Task WorkflowBlocksAtBookmark_AndDoesNotFinalizeDocument()
    {
        var result = await RunWorkflowAsync("DPC-10-ME-0001", revision: 2);
        var contexts = AssertWorkflowIsBlocked(result);

        var prepareContext = Assert.Single(contexts, context => context.Activity.Name == "PrepareDocumentReview");
        Assert.Equal(ActivityStatus.Completed, prepareContext.Status);
        var waitContext = Assert.Single(contexts, context => context.Activity is WaitForDocumentReviewActivity);
        Assert.Equal(ActivityStatus.Running, waitContext.Status);
        Assert.Equal(1L, waitContext.ExecutionCount);
        Assert.Equal(prepareContext.Id, waitContext.SchedulingActivityExecutionId);
        Assert.DoesNotContain(contexts, context => context.Activity.Name == "FinalizeDocument");
        Assert.Equal("DPC-10-ME-0001", waitContext.GetInputs()["DocumentNumber"]);
        Assert.Equal(2, waitContext.GetInputs()["Revision"]);
        Assert.Equal(ReviewKey, waitContext.GetInputs()["ReviewKey"]);
    }

    [Fact]
    public async Task BookmarkCarriesDocumentReviewAndElsaExecutionIdentity()
    {
        var result = await RunWorkflowAsync("DPC-10-ME-0001", revision: 2);
        var contexts = AssertWorkflowIsBlocked(result);
        var waitContext = Assert.Single(contexts, context => context.Activity is WaitForDocumentReviewActivity);
        var bookmark = Assert.Single(result.WorkflowState.Bookmarks);
        var payload = Assert.IsType<DocumentReviewBookmarkPayload>(bookmark.Payload);

        Assert.Equal("DocumentReview", bookmark.Name);
        Assert.Equal("DPC-10-ME-0001", payload.DocumentNumber);
        Assert.Equal(2, payload.Revision);
        Assert.Equal(ReviewKey, payload.ReviewKey);
        Assert.Equal(ReviewKey, bookmark.Metadata!["ReviewKey"]);
        Assert.Equal(waitContext.Activity.Id, bookmark.ActivityId);
        Assert.Equal(waitContext.Activity.NodeId, bookmark.ActivityNodeId);
        Assert.Equal(waitContext.Id, bookmark.ActivityInstanceId);
        Assert.Contains(bookmark, waitContext.Bookmarks);
        Assert.Empty(waitContext.JournalData);
        Assert.Null(bookmark.CallbackMethodName);
        Assert.True(bookmark.AutoBurn);
        Assert.True(bookmark.AutoComplete);
        Assert.Equal(result.WorkflowExecutionContext.Id, result.WorkflowState.Id);
        Assert.NotEmpty(bookmark.Id);
        Assert.NotEmpty(bookmark.Hash);
        Assert.NotEqual(default, bookmark.CreatedAt);
    }

    [Fact]
    public async Task IndependentWorkflowRunsKeepBookmarkAndExecutionStateSeparate()
    {
        var services = CreateServices();
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var workflowBuilder = scope.ServiceProvider.GetRequiredService<IWorkflowBuilderFactory>().CreateBuilder();
        var workflow = await workflowBuilder.BuildWorkflowAsync<DocumentReviewBlockingWorkflow>();
        var workflowGraph = await scope.ServiceProvider.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var first = await runner.RunAsync(workflowGraph, CreateOptions("DPC-10-ME-0001", revision: 2));
        var second = await runner.RunAsync(workflowGraph, CreateOptions("DPC-10-ME-0002", revision: 5));
        var firstContexts = AssertWorkflowIsBlocked(first);
        var secondContexts = AssertWorkflowIsBlocked(second);
        var firstWait = Assert.Single(firstContexts, context => context.Activity is WaitForDocumentReviewActivity);
        var secondWait = Assert.Single(secondContexts, context => context.Activity is WaitForDocumentReviewActivity);
        var firstBookmark = Assert.Single(first.WorkflowState.Bookmarks);
        var secondBookmark = Assert.Single(second.WorkflowState.Bookmarks);

        Assert.Same(firstWait.Activity, secondWait.Activity);
        Assert.NotSame(firstWait, secondWait);
        Assert.NotEqual(firstWait.Id, secondWait.Id);
        Assert.NotEqual(first.WorkflowState.Id, second.WorkflowState.Id);
        Assert.NotEqual(firstBookmark.Id, secondBookmark.Id);
        Assert.NotEqual(firstBookmark.Hash, secondBookmark.Hash);
        Assert.Equal("DPC-10-ME-0001", Assert.IsType<DocumentReviewBookmarkPayload>(firstBookmark.Payload).DocumentNumber);
        Assert.Equal(2, Assert.IsType<DocumentReviewBookmarkPayload>(firstBookmark.Payload).Revision);
        Assert.Equal("DPC-10-ME-0002", Assert.IsType<DocumentReviewBookmarkPayload>(secondBookmark.Payload).DocumentNumber);
        Assert.Equal(5, Assert.IsType<DocumentReviewBookmarkPayload>(secondBookmark.Payload).Revision);
        Assert.Equal("DPC-10-ME-0001", first.WorkflowExecutionContext.Input["DocumentNumber"]);
        Assert.Equal("DPC-10-ME-0002", second.WorkflowExecutionContext.Input["DocumentNumber"]);
        Assert.Single(first.WorkflowState.Bookmarks);
        Assert.Single(second.WorkflowState.Bookmarks);
    }

    private static ActivityExecutionContext[] AssertWorkflowIsBlocked(RunWorkflowResult result)
    {
        Assert.Equal(WorkflowStatus.Running, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, result.WorkflowExecutionContext.SubStatus);

        var contexts = result.Journal.ActivityExecutionContexts.ToArray();
        Assert.DoesNotContain(contexts, context => context.Status == ActivityStatus.Faulted);
        var rootContext = Assert.Single(contexts, context => string.IsNullOrEmpty(context.Activity.Name));
        Assert.Equal(ActivityStatus.Running, rootContext.Status);
        var sequenceContext = Assert.Single(contexts, context => context.Activity.Name == "DocumentReviewSequence");
        Assert.Equal(ActivityStatus.Running, sequenceContext.Status);
        Assert.Single(contexts, context => context.Activity.Name == "PrepareDocumentReview");
        var waitContext = Assert.Single(contexts, context => context.Activity is WaitForDocumentReviewActivity);
        Assert.Equal(ActivityStatus.Running, waitContext.Status);
        Assert.DoesNotContain(contexts, context => context.Activity.Name == "FinalizeDocument");
        Assert.Single(result.WorkflowExecutionContext.Bookmarks);
        Assert.Single(result.WorkflowState.Bookmarks);
        Assert.Equal(
            Assert.Single(result.WorkflowExecutionContext.Bookmarks).Id,
            Assert.Single(result.WorkflowState.Bookmarks).Id);

        return contexts;
    }

    private static async Task<RunWorkflowResult> RunWorkflowAsync(string documentNumber, int revision)
    {
        var services = CreateServices();
        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        return await runner.RunAsync(new DocumentReviewBlockingWorkflow(), CreateOptions(documentNumber, revision));
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

    private static RunWorkflowOptions CreateOptions(string documentNumber, int revision) => new()
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = documentNumber,
            ["Revision"] = revision,
            ["ReviewKey"] = ReviewKey
        }
    };
}
