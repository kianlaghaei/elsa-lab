using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public class DocumentRevisionWorkflowTests
{
    private const string DocumentNumber = "DPC-10-ME-0001";

    [Fact]
    public async Task ZeroCommentRounds_ReviewsOnceAndApprovesWithoutRevision()
    {
        var result = await RunWorkflowAsync(initialRevision: 0, commentRoundsBeforeApproval: 0);
        var contexts = AssertWorkflowSucceeded(result);

        AssertNodeExecutionCounts(contexts, reviewCount: 1, commentCount: 0, revisionCount: 0, approvalCount: 1);
        Assert.Equal("Approved", result.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal(0, Assert.IsType<int>(result.WorkflowState.Output["FinalRevision"]));
        Assert.Equal(1, Assert.IsType<int>(result.WorkflowState.Output["ReviewRounds"]));
        Assert.Equal("Review round 1 — Revision 0", result.WorkflowState.Output["LastReviewSummary"]);
        Assert.Equal("DPC-10-ME-0001 approved at revision 0 after 1 review rounds", result.Result);
        Assert.Equal(new[] { "False" }, GetOutcomes(contexts, "HasComments"));
    }

    [Fact]
    public async Task TwoCommentRounds_ExecutesCycleThreeTimesAndReadsLatestOutput()
    {
        var result = await RunWorkflowAsync(initialRevision: 0, commentRoundsBeforeApproval: 2);
        var contexts = AssertWorkflowSucceeded(result);

        AssertNodeExecutionCounts(contexts, reviewCount: 3, commentCount: 2, revisionCount: 2, approvalCount: 1);
        Assert.Equal("Approved", result.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal(2, Assert.IsType<int>(result.WorkflowState.Output["FinalRevision"]));
        Assert.Equal(3, Assert.IsType<int>(result.WorkflowState.Output["ReviewRounds"]));
        Assert.Equal("Review round 3 — Revision 2", result.WorkflowState.Output["LastReviewSummary"]);
        Assert.Equal("DPC-10-ME-0001 approved at revision 2 after 3 review rounds", result.Result);
        Assert.Equal(new[] { "True", "True", "False" }, GetOutcomes(contexts, "HasComments"));

        var reviewContexts = GetContexts(contexts, "ReviewDocument");
        Assert.Equal(
            new[] { 0, 1, 2 },
            reviewContexts.Select(context => Assert.IsType<int>(context.GetInputs()["CurrentRevision"])));
        Assert.Equal(
            new[] { 1, 2, 3 },
            reviewContexts.Select(context => Assert.IsType<int>(context.GetInputs()["ReviewRound"])));
        Assert.Equal(
            new[] { "Reviewing", "Reviewing", "Reviewing" },
            reviewContexts.Select(context => Assert.IsType<string>(context.GetInputs()["ProcessingStatus"])));

        var expectedSummaries = new[]
        {
            "Review round 1 — Revision 0",
            "Review round 2 — Revision 1",
            "Review round 3 — Revision 2"
        };
        Assert.Equal(expectedSummaries, reviewContexts.Select(context =>
            Assert.IsType<string>(context.GetOutputs()[nameof(ReviewDocumentActivity.ReviewSummary)])));
        Assert.Equal(expectedSummaries, GetContexts(contexts, "LogReviewSummary").Select(context =>
            Assert.IsType<string>(context.GetInputs()["Text"])));

        // One code-first Activity definition/node is scheduled repeatedly, with a fresh journal context per execution.
        Assert.Same(reviewContexts[0].Activity, reviewContexts[1].Activity);
        Assert.Same(reviewContexts[1].Activity, reviewContexts[2].Activity);
        Assert.Single(reviewContexts.Select(context => context.Activity.Id).Distinct());
        Assert.Single(reviewContexts.Select(context => context.Activity.NodeId).Distinct());
        Assert.Equal(3, reviewContexts.Select(context => context.Id).Distinct().Count());
        Assert.NotSame(reviewContexts[0], reviewContexts[1]);
        Assert.NotSame(reviewContexts[1], reviewContexts[2]);
        Assert.All(reviewContexts, context => Assert.Equal(1L, context.ExecutionCount));

        var markReviewingContexts = GetContexts(contexts, "MarkReviewing");
        var logRevisionContexts = GetContexts(contexts, "LogRevision");
        Assert.Equal(3, markReviewingContexts.Length);
        Assert.Equal(2, logRevisionContexts.Length);
        for (var index = 0; index < reviewContexts.Length; index++)
            Assert.Equal(markReviewingContexts[index].Id, reviewContexts[index].SchedulingActivityExecutionId);
        for (var index = 0; index < logRevisionContexts.Length; index++)
            Assert.Equal(logRevisionContexts[index].Id, markReviewingContexts[index + 1].SchedulingActivityExecutionId);

        var flowchart = Assert.IsType<Flowchart>(Assert.Single(contexts, context => context.Activity is Flowchart).Activity);
        Assert.Contains(flowchart.Connections, connection =>
            connection.Source.Activity.Name == "LogRevision" &&
            connection.Target.Activity.Name == "MarkReviewing");
    }

    [Fact]
    public async Task NonZeroInitialRevision_UsesRuntimeRevisionAcrossCycle()
    {
        var result = await RunWorkflowAsync(initialRevision: 5, commentRoundsBeforeApproval: 1);
        var contexts = AssertWorkflowSucceeded(result);

        AssertNodeExecutionCounts(contexts, reviewCount: 2, commentCount: 1, revisionCount: 1, approvalCount: 1);
        Assert.Equal(6, Assert.IsType<int>(result.WorkflowState.Output["FinalRevision"]));
        Assert.Equal(2, Assert.IsType<int>(result.WorkflowState.Output["ReviewRounds"]));
        Assert.Equal("Approved", result.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal("DPC-10-ME-0001 approved at revision 6 after 2 review rounds", result.Result);
        Assert.Equal(
            new[] { 5, 6 },
            GetContexts(contexts, "ReviewDocument").Select(context =>
                Assert.IsType<int>(context.GetInputs()["CurrentRevision"])));
        Assert.Equal(new[] { "True", "False" }, GetOutcomes(contexts, "HasComments"));
    }

    private static async Task<RunWorkflowResult<string>> RunWorkflowAsync(
        int initialRevision,
        int commentRoundsBeforeApproval)
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa =>
        {
            elsa.AddActivity<ReviewDocumentActivity>();
            elsa.AddWorkflow<DocumentRevisionWorkflow>();
        });

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        return await workflowRunner.RunAsync(
            new DocumentRevisionWorkflow(),
            new RunWorkflowOptions
            {
                Input = new Dictionary<string, object>
                {
                    ["DocumentNumber"] = DocumentNumber,
                    ["InitialRevision"] = initialRevision,
                    ["CommentRoundsBeforeApproval"] = commentRoundsBeforeApproval
                }
            }.WithTokenBasedFlowchart());
    }

    private static ActivityExecutionContext[] AssertWorkflowSucceeded(RunWorkflowResult<string> result)
    {
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);

        var contexts = result.Journal.ActivityExecutionContexts.ToArray();
        Assert.DoesNotContain(contexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Equal(ActivityStatus.Completed, Assert.Single(contexts, context => context.Activity is Flowchart).Status);
        Assert.Single(contexts, context => context.Activity.Name == "CompleteDocument");
        return contexts;
    }

    private static void AssertNodeExecutionCounts(
        IReadOnlyCollection<ActivityExecutionContext> contexts,
        int reviewCount,
        int commentCount,
        int revisionCount,
        int approvalCount)
    {
        Assert.Equal(reviewCount, GetContexts(contexts, "ReviewDocument").Length);
        Assert.Equal(reviewCount, GetContexts(contexts, "IncrementReviewRound").Length);
        Assert.Equal(commentCount, GetContexts(contexts, "SetComments").Length);
        Assert.Equal(commentCount, GetContexts(contexts, "LogComments").Length);
        Assert.Equal(revisionCount, GetContexts(contexts, "IncrementRevision").Length);
        Assert.Equal(revisionCount, GetContexts(contexts, "SetRevised").Length);
        Assert.Equal(revisionCount, GetContexts(contexts, "LogRevision").Length);
        Assert.Equal(approvalCount, GetContexts(contexts, "SetApproved").Length);
        Assert.Equal(approvalCount, GetContexts(contexts, "LogApproved").Length);
    }

    private static ActivityExecutionContext[] GetContexts(
        IEnumerable<ActivityExecutionContext> contexts,
        string activityName) => contexts
        .Where(context => context.Activity.Name == activityName)
        .OrderBy(context => context.StartedAt)
        .ToArray();

    private static string[] GetOutcomes(
        IEnumerable<ActivityExecutionContext> contexts,
        string activityName) => GetContexts(contexts, activityName)
        .SelectMany(context => Assert.IsType<string[]>(context.JournalData["Outcomes"]))
        .ToArray();
}
