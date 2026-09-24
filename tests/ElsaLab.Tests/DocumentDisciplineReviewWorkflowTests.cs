using System.Collections.Concurrent;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public class DocumentDisciplineReviewWorkflowTests
{
    private const string DocumentNumber = "DPC-10-ME-0001";

    [Fact]
    public async Task AllBranchesRun_CaptureDistinctOutputs_AndConsolidateOnce()
    {
        using var serviceProvider = CreateServiceProvider(new DocumentDisciplineReviewService());
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await workflowRunner.RunAsync(
            new DocumentDisciplineReviewWorkflow(),
            CreateOptions());

        var contexts = AssertWorkflowSucceeded(result);
        AssertReviewOutput(contexts, "ReviewProcess", "Process", "Process reviewed DPC-10-ME-0001");
        AssertReviewOutput(contexts, "ReviewMechanical", "Mechanical", "Mechanical reviewed DPC-10-ME-0001");
        AssertReviewOutput(contexts, "ReviewInstrument", "Instrument", "Instrument reviewed DPC-10-ME-0001");
        Assert.Equal("Process reviewed DPC-10-ME-0001", result.WorkflowState.Output["ProcessReview"]);
        Assert.Equal("Mechanical reviewed DPC-10-ME-0001", result.WorkflowState.Output["MechanicalReview"]);
        Assert.Equal("Instrument reviewed DPC-10-ME-0001", result.WorkflowState.Output["InstrumentReview"]);
        Assert.Equal("AllReviewsCompleted for DPC-10-ME-0001", result.WorkflowState.Output["ConsolidatedStatus"]);
        Assert.Equal("DPC-10-ME-0001 completed 3 discipline reviews", result.Result);

        var consolidateContext = Assert.Single(contexts, context => context.Activity.Name == "ConsolidateReviews");
        Assert.Equal("Process reviewed DPC-10-ME-0001", consolidateContext.GetInputs()["ProcessReview"]);
        Assert.Equal("Mechanical reviewed DPC-10-ME-0001", consolidateContext.GetInputs()["MechanicalReview"]);
        Assert.Equal("Instrument reviewed DPC-10-ME-0001", consolidateContext.GetInputs()["InstrumentReview"]);
        Assert.Equal(ActivityStatus.Completed, consolidateContext.Status);
    }

    [Fact]
    public async Task WaitAllJoin_DoesNotConsolidateUntilBlockedMechanicalBranchCompletes()
    {
        var reviewService = new ControlledReviewService();
        using var serviceProvider = CreateServiceProvider(reviewService);
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var workflowTask = workflowRunner.RunAsync(
            new DocumentDisciplineReviewWorkflow(),
            CreateOptions());

        try
        {
            await reviewService.MechanicalStarted.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new[] { "Process", "Instrument", "Mechanical" }, reviewService.Started.ToArray());
            Assert.Equal(new[] { "Process", "Instrument" }, reviewService.Completed.ToArray());
            Assert.False(reviewService.ConsolidationStarted.IsCompleted);
            Assert.False(workflowTask.IsCompleted);
            Assert.Equal(1, reviewService.MaximumActiveReviews);
        }
        finally
        {
            reviewService.ReleaseMechanical();
        }

        var result = await workflowTask.WaitAsync(TimeSpan.FromSeconds(10));
        var contexts = AssertWorkflowSucceeded(result);

        Assert.Equal(new[] { "Process", "Instrument", "Mechanical" }, reviewService.Completed.ToArray());
        Assert.Equal(1, reviewService.ConsolidationCallCount);
        Assert.True(reviewService.ConsolidationStarted.IsCompleted);
        Assert.Single(contexts, context => context.Activity.Name == "ConsolidateReviews");
        Assert.Equal("AllReviewsCompleted for DPC-10-ME-0001", result.WorkflowState.Output["ConsolidatedStatus"]);
    }

    [Fact]
    public async Task BranchNodesHaveDistinctGraphIdentity_AndJoinSchedulesOneContinuation()
    {
        var reviewService = new RecordingReviewService();
        using var serviceProvider = CreateServiceProvider(reviewService);
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        var firstResult = await workflowRunner.RunAsync(new DocumentDisciplineReviewWorkflow(), CreateOptions());
        var firstContexts = AssertWorkflowSucceeded(firstResult);
        var firstOrder = reviewService.Started.ToArray();
        reviewService.Reset();

        var secondResult = await workflowRunner.RunAsync(new DocumentDisciplineReviewWorkflow(), CreateOptions());
        var secondContexts = AssertWorkflowSucceeded(secondResult);
        var secondOrder = reviewService.Started.ToArray();

        Assert.Equal(new[] { "Process", "Instrument", "Mechanical" }, firstOrder);
        Assert.Equal(firstOrder, secondOrder);

        var firstReviews = GetReviewContexts(firstContexts);
        var secondReviews = GetReviewContexts(secondContexts);
        Assert.Equal(3, firstReviews.Select(context => context.Activity.Id).Distinct().Count());
        Assert.Equal(3, firstReviews.Select(context => context.Activity.NodeId).Distinct().Count());
        Assert.Equal(3, firstReviews.Select(context => context.Id).Distinct().Count());
        Assert.NotSame(firstReviews[0].Activity, firstReviews[1].Activity);
        Assert.NotSame(firstReviews[1].Activity, firstReviews[2].Activity);
        Assert.Equal(3, secondReviews.Select(context => context.Id).Distinct().Count());
        Assert.All(firstReviews.Concat(secondReviews), context => Assert.Equal(1L, context.ExecutionCount));

        var firstFlowchart = Assert.Single(firstContexts, context => context.Activity is Flowchart);
        var firstFork = Assert.Single(firstContexts, context => context.Activity is FlowFork);
        var firstJoin = Assert.Single(firstContexts, context => context.Activity is FlowJoin);
        var firstConsolidate = Assert.Single(firstContexts, context => context.Activity.Name == "ConsolidateReviews");
        var firstComplete = Assert.Single(firstContexts, context => context.Activity.Name == "CompleteDocument");
        Assert.All(firstReviews, context => Assert.Equal(firstFlowchart.Id, context.ParentActivityExecutionContext?.Id));
        Assert.All(firstReviews, context => Assert.Equal(firstFork.Id, context.SchedulingActivityExecutionId));
        Assert.Equal(new[] { "Process", "Instrument", "Mechanical" },
            Assert.IsType<string[]>(firstFork.JournalData["Outcomes"]));
        Assert.Equal(FlowJoinMode.WaitAll, Assert.IsType<FlowJoinMode>(firstJoin.GetInputs()["Mode"]));
        Assert.Empty(firstJoin.JournalData);
        Assert.Empty((System.Collections.IEnumerable)firstFlowchart.Properties["Flowchart.Tokens"]);
        Assert.Equal(firstReviews[^1].Id, firstJoin.SchedulingActivityExecutionId);
        Assert.Equal(firstJoin.Id, firstConsolidate.SchedulingActivityExecutionId);
        Assert.Equal(firstConsolidate.Id, firstComplete.SchedulingActivityExecutionId);
        Assert.Equal(ActivityStatus.Completed, firstJoin.Status);
        Assert.Equal(ActivityStatus.Completed, firstFlowchart.Status);
        Assert.Single(firstContexts, context => context.Activity.Name == "ConsolidateReviews");
        Assert.Single(secondContexts, context => context.Activity.Name == "ConsolidateReviews");
    }

    private static ActivityExecutionContext[] AssertWorkflowSucceeded(RunWorkflowResult<string> result)
    {
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);

        var contexts = result.Journal.ActivityExecutionContexts.OrderBy(context => context.StartedAt).ToArray();
        Assert.DoesNotContain(contexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Equal(ActivityStatus.Completed, Assert.Single(contexts, context => context.Activity is Flowchart).Status);
        Assert.Single(contexts, context => context.Activity is FlowFork);
        Assert.Equal(ActivityStatus.Completed, Assert.Single(contexts, context => context.Activity is FlowJoin).Status);
        Assert.Single(contexts, context => context.Activity.Name == "CompleteDocument");
        return contexts;
    }

    private static void AssertReviewOutput(
        IEnumerable<ActivityExecutionContext> contexts,
        string activityName,
        string discipline,
        string expectedResult)
    {
        var context = Assert.Single(contexts, item => item.Activity.Name == activityName);
        Assert.Equal(discipline, context.GetInputs()["Discipline"]);
        Assert.Equal(expectedResult, context.GetOutputs()[nameof(ReviewDisciplineActivity.ReviewResult)]);
        Assert.Equal(ActivityStatus.Completed, context.Status);
    }

    private static ActivityExecutionContext[] GetReviewContexts(IEnumerable<ActivityExecutionContext> contexts) => contexts
        .Where(context => context.Activity is ReviewDisciplineActivity)
        .OrderBy(context => context.StartedAt)
        .ToArray();

    private static ServiceProvider CreateServiceProvider(IDocumentDisciplineReviewService reviewService)
    {
        var services = new ServiceCollection();
        services.AddSingleton(reviewService);
        services.AddSingleton<IDocumentDisciplineReviewService>(reviewService);
        services.AddElsa(elsa =>
        {
            elsa.AddActivity<ReviewDisciplineActivity>();
            elsa.AddActivity<ConsolidateReviewsActivity>();
            elsa.AddWorkflow<DocumentDisciplineReviewWorkflow>();
        });
        return services.BuildServiceProvider();
    }

    private static RunWorkflowOptions CreateOptions() => new RunWorkflowOptions
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = DocumentNumber
        }
    }.WithTokenBasedFlowchart();

    private sealed class RecordingReviewService : IDocumentDisciplineReviewService
    {
        private readonly ConcurrentQueue<string> _started = new();

        public string[] Started => _started.ToArray();
        public int ConsolidationCallCount { get; private set; }

        public Task<string> ReviewAsync(string documentNumber, string discipline, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _started.Enqueue(discipline);
            return Task.FromResult($"{discipline} reviewed {documentNumber}");
        }

        public Task<string> ConsolidateAsync(
            string documentNumber,
            string processReview,
            string mechanicalReview,
            string instrumentReview,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConsolidationCallCount++;
            return Task.FromResult($"AllReviewsCompleted for {documentNumber}");
        }

        public void Reset()
        {
            while (_started.TryDequeue(out _))
            {
            }

            ConsolidationCallCount = 0;
        }
    }

    private sealed class ControlledReviewService : IDocumentDisciplineReviewService
    {
        private readonly ConcurrentQueue<string> _started = new();
        private readonly ConcurrentQueue<string> _completed = new();
        private readonly TaskCompletionSource _mechanicalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseMechanical = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _consolidationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeReviews;
        private int _maximumActiveReviews;
        private int _consolidationCallCount;

        public string[] Started => _started.ToArray();
        public string[] Completed => _completed.ToArray();
        public Task MechanicalStarted => _mechanicalStarted.Task;
        public Task ConsolidationStarted => _consolidationStarted.Task;
        public int MaximumActiveReviews => Volatile.Read(ref _maximumActiveReviews);
        public int ConsolidationCallCount => Volatile.Read(ref _consolidationCallCount);

        public async Task<string> ReviewAsync(string documentNumber, string discipline, CancellationToken cancellationToken)
        {
            var activeReviews = Interlocked.Increment(ref _activeReviews);
            UpdateMaximumActiveReviews(activeReviews);
            _started.Enqueue(discipline);

            try
            {
                await Task.Yield();
                if (discipline == "Mechanical")
                {
                    _mechanicalStarted.TrySetResult();
                    await _releaseMechanical.Task.WaitAsync(cancellationToken);
                }

                _completed.Enqueue(discipline);
                return $"{discipline} reviewed {documentNumber}";
            }
            finally
            {
                Interlocked.Decrement(ref _activeReviews);
            }
        }

        public Task<string> ConsolidateAsync(
            string documentNumber,
            string processReview,
            string mechanicalReview,
            string instrumentReview,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _consolidationCallCount);
            _consolidationStarted.TrySetResult();
            return Task.FromResult($"AllReviewsCompleted for {documentNumber}");
        }

        public void ReleaseMechanical() => _releaseMechanical.TrySetResult();

        private void UpdateMaximumActiveReviews(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumActiveReviews);
                if (candidate <= current || Interlocked.CompareExchange(ref _maximumActiveReviews, candidate, current) == current)
                    return;
            }
        }
    }
}
