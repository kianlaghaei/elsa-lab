using Elsa.Extensions;
using Elsa.Resilience;
using Elsa.Resilience.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Activities;
using Elsa.Workflows.Runtime.Notifications;
using Elsa.Workflows.Runtime.Messages;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Capstone;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ElsaLab.Tests;

public sealed class EdmsCapstoneTests
{
    private const string DocumentId = "DOC-CAPSTONE-100";
    private const string DocumentNumber = "DPC-10-ME-0001";
    private const int Revision = 2;
    private const string RevisionId = "DOC-CAPSTONE-100:R2";
    private const string ReviewCycleId = "DOC-CAPSTONE-100:R2:C1";
    private const string FileContent = "Canonical R2 file contents for the EDMS architecture capstone.";
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public EdmsCapstoneTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    public async Task ThreeDisciplineTasks_WaitAll_ThenApprovePublishAndIssueOneTransmittal()
    {
        using var fixture = new CapstoneFixture();
        var start = await fixture.StartV1Async();

        Assert.Equal(WorkflowStatus.Running, start.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, start.SubStatus);
        Assert.Empty(start.WorkflowState.Incidents);
        Assert.DoesNotContain(start.WorkflowState.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
        var tasks = await fixture.TaskStore.FindByReviewCycleAsync(ReviewCycleId, CancellationToken.None);
        Assert.True(tasks.Count == 3,
            $"Expected the dispatcher to create three tasks, got {tasks.Count}; bookmarks={start.WorkflowState.Bookmarks.Count}; contexts=" +
            string.Join(" | ", start.WorkflowState.ActivityExecutionContexts.Select(context => $"{context.ScheduledActivityNodeId}:{context.Status}")));
        Assert.Equal(3, tasks.Select(task => task.BookmarkId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, tasks.Select(task => task.ActivityId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tasks, task => Assert.Equal(DocumentId, task.DocumentId));
        Assert.All(tasks, task =>
        {
            Assert.Equal(start.WorkflowState.Id, task.WorkflowInstanceId);
            Assert.Equal(ReviewTaskStatus.Assigned, task.Status);
            Assert.False(string.IsNullOrWhiteSpace(task.ActivityExecutionId));
            Assert.False(string.IsNullOrWhiteSpace(task.BookmarkId));
        });
        Assert.Equal(3, tasks.Select(task => task.Discipline).Distinct(StringComparer.Ordinal).Count());

        foreach (var task in tasks.OrderBy(task => task.Discipline, StringComparer.Ordinal))
        {
            var reviewer = Assert.Single(task.CandidateUsers);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                fixture.TaskService.ClaimAsync(task.TaskId, "unassigned-user", CancellationToken.None));
            var claimed = await fixture.TaskService.ClaimAsync(task.TaskId, reviewer, CancellationToken.None);
            if (task.Discipline == "Process")
            {
                var unclaimed = await fixture.TaskService.UnclaimAsync(task.TaskId, reviewer, CancellationToken.None);
                Assert.Equal(ReviewTaskStatus.Assigned, unclaimed.Status);
                claimed = await fixture.TaskService.ClaimAsync(task.TaskId, reviewer, CancellationToken.None);
            }
            Assert.Equal(ReviewTaskStatus.Claimed, claimed.Status);
            await fixture.TaskService.CompleteAsync(task.TaskId, reviewer, ReviewDecision.Approved, null, CancellationToken.None);
        }

        var state = await fixture.ExportStateAsync(start.WorkflowState.Id);
        Assert.True(state.Status == WorkflowStatus.Finished,
            $"Expected Finished, got {state.Status}/{state.SubStatus}; incidents=" +
            string.Join(" | ", state.Incidents.Select(incident => $"{incident.Message}; {incident.Exception?.Type}; {incident.Exception?.Message}")) +
            "; contexts=" + string.Join(" | ", state.ActivityExecutionContexts.Select(context =>
                $"{context.ScheduledActivityNodeId}:{context.Status}:state={System.Text.Json.JsonSerializer.Serialize(context.ActivityState)}")));
        Assert.True(state.SubStatus == WorkflowSubStatus.Finished,
            $"Expected Finished substatus, got {state.Status}/{state.SubStatus}; incidents=" +
            string.Join(" | ", state.Incidents.Select(incident => $"{incident.Message}; {incident.Exception?.Type}; {incident.Exception?.Message}")) +
            "; contexts=" + string.Join(" | ", state.ActivityExecutionContexts.Select(context =>
                $"{context.ScheduledActivityNodeId}:{context.Status}:state={System.Text.Json.JsonSerializer.Serialize(context.ActivityState)}")));
        Assert.Empty(state.Incidents);
        Assert.Empty(state.Bookmarks);
        Assert.Equal("Published", state.Output["FinalStatus"]);
        Assert.Equal(true, state.Output["Finalized"]);
        Assert.Equal("V1", state.Output["DefinitionMarker"]);
        Assert.Equal(false, state.Output["CoordinatorCheckExecuted"]);
        Assert.Equal(3, state.Output["ReviewTaskCount"]);
        Assert.Equal(3, state.Output["DistributionCount"]);
        Assert.Equal(0, state.Output["CommentCount"]);
        var distributions = await fixture.CapstoneService.GetDistributionsAsync(RevisionId, CancellationToken.None);
        Assert.Equal(3, distributions.Count);
        Assert.All(distributions, distribution => Assert.Equal(DistributionMode.Reference, distribution.Mode));
        Assert.Equal(3, distributions.Select(distribution => distribution.Workspace).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, fixture.PublicationService.CallCount);
        Assert.Single(fixture.PublicationService.AppliedOperations);
        var issuedTransmittal = Assert.Single(await fixture.CapstoneService.GetTransmittalsAsync(CancellationToken.None));
        var issuedItem = Assert.Single(issuedTransmittal.Items);
        Assert.Equal(RevisionId, issuedItem.DocumentRevisionId);
        Assert.Equal(DocumentId, issuedItem.DocumentId);
        Assert.False(string.IsNullOrWhiteSpace(issuedItem.FileSha256));

        var moveAttempt = Assert.Single(fixture.DocumentRepository.GetAttempts());
        Assert.Equal(start.WorkflowState.Id, moveAttempt.Command.WorkflowInstanceId);
        Assert.False(string.IsNullOrWhiteSpace(moveAttempt.Command.ActivityId));
        Assert.False(string.IsNullOrWhiteSpace(moveAttempt.Command.ActivityExecutionId));
        Assert.Equal($"Move:{DocumentNumber}:R{Revision}:Approved", moveAttempt.Command.OperationId);

        var destination = Path.Combine(fixture.RootPath, "approved", $"{DocumentNumber}-R{Revision}.pdf");
        Assert.False(File.Exists(fixture.SourcePath));
        Assert.True(File.Exists(destination));
        Assert.Equal(FileContent, await File.ReadAllTextAsync(destination));

        const string nextRevisionId = "DOC-CAPSTONE-100:R3";
        fixture.AddIncomingRevision(nextRevisionId, 3, "Next revision cannot mutate the issued transmittal snapshot.");
        await fixture.CapstoneService.ReceiveRevisionAsync(new DocumentRevisionRecord(
            "DEMO-PROJECT", DocumentId, nextRevisionId, DocumentNumber, 3,
            "DOC-CAPSTONE-100:R3:C1", "Incoming", null, null, false), CancellationToken.None);
        var reloadedTransmittal = await fixture.CapstoneService.GetTransmittalAsync(issuedTransmittal.OperationId, CancellationToken.None);
        Assert.Equal(issuedItem, Assert.Single(reloadedTransmittal!.Items));

        Assert.DoesNotContain(state.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
        Assert.All(await fixture.TaskStore.FindByReviewCycleAsync(ReviewCycleId, CancellationToken.None),
            task => Assert.Equal(ReviewTaskStatus.Completed, task.Status));
    }

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    public async Task CommentedR2_RemainsHistorical_WhenR3StartsANewReviewCycle()
    {
        using var fixture = new CapstoneFixture();
        var r2 = await fixture.StartV1Async();
        await CompleteCycleAsync(fixture, ReviewCycleId, new Dictionary<string, ReviewDecision>
        {
            ["Process"] = ReviewDecision.Approved,
            ["Mechanical"] = ReviewDecision.Commented,
            ["Instrument"] = ReviewDecision.Approved
        });

        var r2State = await fixture.ExportStateAsync(r2.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Finished, r2State.Status);
        Assert.Equal(WorkflowSubStatus.Finished, r2State.SubStatus);
        Assert.Equal(true, r2State.Output["CapstoneHasComments"]);
        Assert.Equal("RevisionRequired", r2State.Output["FinalStatus"]);
        var historicalComment = Assert.Single(await fixture.CapstoneService.GetCommentsAsync(RevisionId, CancellationToken.None));
        Assert.Equal(ReviewCycleId, historicalComment.ReviewCycleId);
        Assert.Equal("Mechanical", historicalComment.Discipline);
        Assert.Equal("Please clarify the equipment interface.", historicalComment.Text);

        const string revision3Id = "DOC-CAPSTONE-100:R3";
        const string cycle3Id = "DOC-CAPSTONE-100:R3:C1";
        fixture.AddIncomingRevision(revision3Id, 3, "Canonical R3 file contents.");
        var r3 = await fixture.StartV1Async(revision3Id, 3, cycle3Id);
        await CompleteCycleAsync(fixture, cycle3Id, ApprovedDecisions());

        var r3State = await fixture.ExportStateAsync(r3.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Finished, r3State.Status);
        Assert.Equal(WorkflowSubStatus.Finished, r3State.SubStatus);
        Assert.Equal("Published", r3State.Output["FinalStatus"]);
        Assert.Equal(revision3Id, await fixture.CapstoneService.GetLatestReceivedRevisionIdAsync(DocumentId, CancellationToken.None));
        Assert.Equal(revision3Id, await fixture.CapstoneService.GetCurrentValidRevisionIdAsync(DocumentId, CancellationToken.None));
        Assert.Single(await fixture.CapstoneService.GetCommentsAsync(RevisionId, CancellationToken.None));
        Assert.Empty(await fixture.CapstoneService.GetCommentsAsync(revision3Id, CancellationToken.None));
        Assert.Equal(DocumentId, (await fixture.CapstoneService.GetRevisionAsync(revision3Id, CancellationToken.None)).DocumentId);
    }

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    public async Task V1SuspendedBeforeV2_ResumesOnV1_WhileNewInstanceUsesV2()
    {
        using var fixture = new CapstoneFixture();
        var oldV1 = await fixture.StartV1Async();
        const string v2DocumentId = "DOC-CAPSTONE-V2-101";
        const string v2DocumentNumber = "DPC-20-EL-0002";
        const string v2RevisionId = "DOC-CAPSTONE-V2-101:R2";
        const string v2CycleId = "DOC-CAPSTONE-V2-101:R2:C1";
        fixture.AddIncomingRevision(v2RevisionId, Revision, "V2 canonical file.", v2DocumentId, v2DocumentNumber);
        var newV2 = await fixture.StartV2Async(v2DocumentId, v2DocumentNumber, v2RevisionId, v2CycleId);

        await CompleteCycleAsync(fixture, v2CycleId, ApprovedDecisions());
        var v2State = await fixture.ExportStateAsync(newV2.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Finished, v2State.Status);
        Assert.Equal("V2", v2State.Output["DefinitionMarker"]);
        Assert.Equal(true, v2State.Output["CoordinatorCheckExecuted"]);

        await CompleteCycleAsync(fixture, ReviewCycleId, ApprovedDecisions());
        var v1State = await fixture.ExportStateAsync(oldV1.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Finished, v1State.Status);
        Assert.Equal(WorkflowSubStatus.Finished, v1State.SubStatus);
        Assert.Equal("V1", v1State.Output["DefinitionMarker"]);
        Assert.Equal(false, v1State.Output["CoordinatorCheckExecuted"]);
        Assert.Empty(v1State.Incidents);
    }

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    public async Task ReviewCompletesBeforeSla_AndReminderEscalationDoNotRunLater()
    {
        using var fixture = new CapstoneFixture();
        var start = await fixture.StartSlaWorkflowAsync("capstone-sla-on-time", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
        Assert.Equal(WorkflowStatus.Running, start.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, start.SubStatus);
        Assert.Equal(2, start.WorkflowState.Bookmarks.Count);

        var task = Assert.Single(await fixture.TaskStore.FindByWorkflowAsync(start.WorkflowState.Id, CancellationToken.None));
        var reviewer = Assert.Single(task.CandidateUsers);
        await fixture.TaskService.ClaimAsync(task.TaskId, reviewer, CancellationToken.None);
        await fixture.TaskService.CompleteAsync(task.TaskId, reviewer, ReviewDecision.Approved, null, CancellationToken.None);

        var completed = await fixture.ExportStateAsync(start.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Finished, completed.Status);
        Assert.Equal(WorkflowSubStatus.Finished, completed.SubStatus);
        Assert.Empty(completed.Incidents);
        Assert.Equal("OnTime", completed.Output["FinalSlaStatus"]);
        Assert.Empty(completed.Bookmarks);
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Empty(fixture.SlaActionService.Actions);
        Assert.Empty((await fixture.ExportStateAsync(start.WorkflowState.Id)).Bookmarks);
    }

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    public async Task WithdrawalCancelsPendingTaskAndTimerBeforeTheyCanCreateActions()
    {
        using var fixture = new CapstoneFixture();
        var start = await fixture.StartSlaWorkflowAsync("capstone-sla-cancel", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var task = Assert.Single(await fixture.TaskStore.FindByWorkflowAsync(start.WorkflowState.Id, CancellationToken.None));
        Assert.Equal(2, start.WorkflowState.Bookmarks.Count);

        await (await fixture.Services.GetRequiredService<IWorkflowRuntime>()
                .CreateClientAsync(start.WorkflowState.Id))
            .CancelAsync();
        await fixture.TaskService.CancelWorkflowTasksAsync(start.WorkflowState.Id, CancellationToken.None);

        var cancelled = await fixture.ExportStateAsync(start.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Finished, cancelled.Status);
        Assert.Equal(WorkflowSubStatus.Cancelled, cancelled.SubStatus);
        Assert.Equal(ReviewTaskStatus.Cancelled, (await fixture.TaskStore.FindAsync(task.TaskId, CancellationToken.None))!.Status);
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Empty(fixture.SlaActionService.Actions);
        Assert.Empty((await fixture.ExportStateAsync(start.WorkflowState.Id)).Bookmarks);
    }

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    [Trait("Category", "ConcurrencySmoke")]
    public async Task OneHundredIndependentReviews_CompleteWithoutCrossInstanceState()
    {
        using var fixture = new CapstoneFixture();
        var started = await fixture.StartBatchAsync(100);
        Assert.Equal(100, started.Count);
        Assert.Equal(100, started.Select(item => item.WorkflowState.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(started, item =>
        {
            Assert.Equal(WorkflowStatus.Running, item.WorkflowState.Status);
            Assert.Equal(WorkflowSubStatus.Suspended, item.SubStatus);
            Assert.Equal(3, item.WorkflowState.Bookmarks.Count);
        });

        var stopwatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(started, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (run, token) =>
        {
            var tasks = await fixture.TaskStore.FindByWorkflowAsync(run.WorkflowState.Id, token);
            Assert.Equal(3, tasks.Count);
            foreach (var task in tasks.OrderBy(task => task.Discipline, StringComparer.Ordinal))
            {
                var reviewer = task.CandidateUsers.Single();
                await fixture.TaskService.ClaimAsync(task.TaskId, reviewer, token);
                await fixture.TaskService.CompleteAsync(
                    task.TaskId,
                    reviewer,
                    task.Discipline == "Mechanical" ? ReviewDecision.Commented : ReviewDecision.Approved,
                    task.Discipline == "Mechanical" ? "Capstone concurrency smoke comment." : null,
                    token);
            }
        });
        stopwatch.Stop();

        var allTasks = new List<EdmsReviewTask>(300);
        foreach (var run in started)
        {
            var state = await fixture.ExportStateAsync(run.WorkflowState.Id);
            Assert.Equal(WorkflowStatus.Finished, state.Status);
            Assert.Equal(WorkflowSubStatus.Finished, state.SubStatus);
            Assert.Empty(state.Incidents);
            Assert.Equal(run.DocumentId, state.Output["DocumentId"]);
            Assert.Equal("RevisionRequired", state.Output["FinalStatus"]);
            var tasks = await fixture.TaskStore.FindByWorkflowAsync(run.WorkflowState.Id, CancellationToken.None);
            Assert.All(tasks, task => Assert.Equal(ReviewTaskStatus.Completed, task.Status));
            Assert.All(tasks, task => Assert.Equal(run.WorkflowState.Id, task.WorkflowInstanceId));
            allTasks.AddRange(tasks);
        }

        Assert.Equal(300, allTasks.Count);
        Assert.Equal(300, allTasks.Select(task => task.TaskId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(100, allTasks.Select(task => task.DocumentRevisionId).Distinct(StringComparer.Ordinal).Count());
        _output.WriteLine($"Concurrency smoke: 100 workflows, 300 persisted test tasks, 300 successful bookmark resumes, 8 workflow-level workers, {stopwatch.Elapsed.TotalSeconds:F2}s; all finished without cross-instance state or incidents.");
    }

    [Fact]
    [Trait("Category", "EdmsCapstone")]
    public async Task DistributionModes_ReferenceCopyAndMoveHaveDistinctFileSemantics()
    {
        using var fixture = new CapstoneFixture();
        foreach (var mode in Enum.GetValues<DistributionMode>())
        {
            var suffix = mode.ToString().ToUpperInvariant();
            var documentId = $"CAPSTONE-DIST-{suffix}";
            var documentNumber = $"DPC-91-PR-{suffix}";
            var revisionId = $"{documentId}:R2";
            var cycleId = $"{revisionId}:C1";
            var content = $"{mode} distribution content";
            fixture.AddIncomingRevision(revisionId, Revision, content, documentId, documentNumber);
            await fixture.CapstoneService.ReceiveRevisionAsync(
                new DocumentRevisionRecord("DEMO-PROJECT", documentId, revisionId, documentNumber,
                    Revision, cycleId, "Incoming", null, null, false), CancellationToken.None);
            var revision = await fixture.CapstoneService.GetRevisionAsync(revisionId, CancellationToken.None);

            var first = await fixture.CapstoneService.DistributeAsync($"Distribute-{mode}", revision,
                ["Process", "Mechanical"], mode, CancellationToken.None);
            var second = await fixture.CapstoneService.DistributeAsync($"Distribute-{mode}", revision,
                ["Process", "Mechanical"], mode, CancellationToken.None);

            Assert.Equal(first, second);
            Assert.Equal(2, first.Count);
            Assert.All(first, distribution => Assert.Equal(mode, distribution.Mode));
            var canonicalPath = Path.Combine(fixture.RootPath, "incoming", $"{documentNumber}-R{Revision}.pdf");
            if (mode == DistributionMode.Reference)
            {
                Assert.True(File.Exists(canonicalPath));
                Assert.All(first, distribution => Assert.Contains("/canonical", distribution.CanonicalFileIdentity, StringComparison.Ordinal));
            }
            else if (mode == DistributionMode.Copy)
            {
                Assert.True(File.Exists(canonicalPath));
                foreach (var distribution in first)
                    Assert.Equal(content, await File.ReadAllTextAsync(distribution.CanonicalFileIdentity));
            }
            else
            {
                Assert.False(File.Exists(canonicalPath));
                var sharedIdentity = Assert.Single(first.Select(distribution => distribution.CanonicalFileIdentity).Distinct(StringComparer.Ordinal));
                Assert.Equal(content, await File.ReadAllTextAsync(sharedIdentity));
            }
        }
    }

    private static Dictionary<string, ReviewDecision> ApprovedDecisions() => new(StringComparer.Ordinal)
    {
        ["Process"] = ReviewDecision.Approved,
        ["Mechanical"] = ReviewDecision.Approved,
        ["Instrument"] = ReviewDecision.Approved
    };

    private static async Task CompleteCycleAsync(
        CapstoneFixture fixture,
        string reviewCycleId,
        IReadOnlyDictionary<string, ReviewDecision> decisions)
    {
        var tasks = await fixture.TaskStore.FindByReviewCycleAsync(reviewCycleId, CancellationToken.None);
        Assert.Equal(3, tasks.Count);
        foreach (var task in tasks.OrderBy(task => task.Discipline, StringComparer.Ordinal))
        {
            var reviewer = Assert.Single(task.CandidateUsers);
            await fixture.TaskService.ClaimAsync(task.TaskId, reviewer, CancellationToken.None);
            await fixture.TaskService.CompleteAsync(
                task.TaskId,
                reviewer,
                decisions[task.Discipline],
                decisions[task.Discipline] == ReviewDecision.Commented ? "Please clarify the equipment interface." : null,
                CancellationToken.None);
        }

        var completedTasks = await fixture.TaskStore.FindByReviewCycleAsync(reviewCycleId, CancellationToken.None);
        Assert.All(completedTasks, task => Assert.Equal(decisions[task.Discipline], task.Decision));
    }

    private sealed class CapstoneFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly InMemoryDocumentRepository _documentRepository;

        public CapstoneFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"elsalab-edms-capstone-{Guid.NewGuid():N}");
            SourcePath = Path.Combine(RootPath, "incoming", $"{DocumentNumber}-R{Revision}.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(SourcePath)!);
            File.WriteAllText(SourcePath, FileContent);

            _documentRepository = new InMemoryDocumentRepository();
            _documentRepository.RegisterIncoming(new DocumentMetadata(
                RevisionId, DocumentNumber, Revision, DocumentStorageLocation.Incoming, SourcePath, "Incoming", null));
            var storageOptions = new DocumentStorageOptions(RootPath);
            var taskStore = new InMemoryEdmsReviewTaskStore();
            var capstoneService = new InMemoryEdmsCapstoneService(taskStore, storageOptions);
            PublicationService = new CapstonePublicationService();
            SlaActionService = new InMemoryReviewSlaActionService();

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Strategies:0:$type"] = nameof(DocumentPublicationRetryStrategy),
                ["Resilience:Strategies:0:Id"] = "document-publication",
                ["Resilience:Strategies:0:DisplayName"] = "Capstone publication retry",
                ["Resilience:Strategies:0:MaxRetryAttempts"] = "2",
                ["Resilience:Strategies:0:Delay"] = "00:00:00.005"
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(taskStore);
            services.AddSingleton<IEdmsReviewTaskStore>(taskStore);
            services.AddSingleton<IEdmsCapstoneService>(capstoneService);
            services.AddSingleton<IEdmsReviewTaskApplicationService, EdmsReviewTaskApplicationService>();
            services.AddSingleton(_documentRepository);
            services.AddSingleton(storageOptions);
            services.AddSingleton<IDocumentStorageService, FileSystemDocumentStorageService>();
            services.AddSingleton<IDocumentPublicationService>(PublicationService);
            services.AddSingleton<IReviewSlaActionService>(SlaActionService);
            services.AddSingleton<ITransientExceptionStrategy, DocumentServiceTransientExceptionStrategy>();
            services.AddElsa(elsa =>
            {
                elsa.UseResilience(resilience => resilience.AddResilienceStrategyType<DocumentPublicationRetryStrategy>());
                elsa.AddActivity<RegisterCapstoneRevisionActivity>();
                elsa.AddActivity<DistributeCapstoneRevisionActivity>();
                elsa.AddActivity<ConsolidateCapstoneReviewsActivity>();
                elsa.AddActivity<MarkCapstoneRevisionRequiredActivity>();
                elsa.AddActivity<MarkCapstoneRevisionPublishedActivity>();
                elsa.AddActivity<EdmsCoordinatorCheckActivity>();
                elsa.AddActivity<CreateCapstoneTransmittalActivity>();
                elsa.AddActivity<CompleteCapstoneWorkflowActivity>();
                elsa.AddActivity<ApproveCapstoneRevisionActivity>();
                elsa.AddActivity<MoveDocumentActivity>();
                elsa.AddActivity<PublishDocumentActivity>();
                elsa.AddActivity<SendReviewReminderActivity>();
                elsa.AddActivity<EscalateReviewActivity>();
                elsa.AddWorkflow<EdmsEngineeringReviewV1Workflow>();
                elsa.AddWorkflow<EdmsEngineeringReviewV2Workflow>();
                elsa.AddWorkflow<EdmsReviewSlaRaceWorkflow>();
            });
            services.AddSingleton<ITaskDispatcher, EdmsReviewTaskDispatcher>();
            _provider = services.BuildServiceProvider();
            TaskStore = taskStore;
            TaskService = _provider.GetRequiredService<IEdmsReviewTaskApplicationService>();
            CapstoneService = capstoneService;
            DocumentRepository = _documentRepository;
        }

        public string RootPath { get; }
        public string SourcePath { get; }
        public InMemoryEdmsReviewTaskStore TaskStore { get; }
        public IEdmsReviewTaskApplicationService TaskService { get; }
        public InMemoryEdmsCapstoneService CapstoneService { get; }
        public InMemoryDocumentRepository DocumentRepository { get; }
        public CapstonePublicationService PublicationService { get; }
        public InMemoryReviewSlaActionService SlaActionService { get; }
        public IServiceProvider Services => _provider;

        public Task<(WorkflowState WorkflowState, WorkflowSubStatus SubStatus)> StartV1Async(
            string revisionId = RevisionId,
            int revision = Revision,
            string reviewCycleId = ReviewCycleId,
            string documentId = DocumentId,
            string documentNumber = DocumentNumber) =>
            StartAsync<EdmsEngineeringReviewV1Workflow>(
                EdmsCapstoneDefinitionIdentity.Version1Id,
                revisionId,
                revision,
                reviewCycleId,
                documentId,
                documentNumber);

        public Task<(WorkflowState WorkflowState, WorkflowSubStatus SubStatus)> StartV2Async(
            string documentId,
            string documentNumber,
            string revisionId,
            string reviewCycleId) =>
            StartAsync<EdmsEngineeringReviewV2Workflow>(
                EdmsCapstoneDefinitionIdentity.Version2Id,
                revisionId,
                Revision,
                reviewCycleId,
                documentId,
                documentNumber);

        public async Task<SlaRun> StartSlaWorkflowAsync(
            string reviewKey,
            TimeSpan initialDelay,
            TimeSpan reminderDelay)
        {
            var workflow = await _provider.GetRequiredService<IWorkflowBuilderFactory>()
                .CreateBuilder().BuildWorkflowAsync<EdmsReviewSlaRaceWorkflow>();
            await _provider.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
            await _provider.GetRequiredService<IWorkflowDefinitionStorePopulator>().PopulateStoreAsync();
            var definition = await _provider.GetRequiredService<IWorkflowDefinitionStore>()
                .FindAsync(new WorkflowDefinitionFilter { DefinitionId = workflow.Identity.DefinitionId })
                ?? throw new InvalidOperationException("The SLA capstone workflow definition was not persisted in the runtime registry.");
            var client = await _provider.GetRequiredService<IWorkflowRuntime>().CreateClientAsync();
            var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionVersionId(definition.Id),
                Input = new Dictionary<string, object>
                {
                    ["ProjectId"] = "DEMO-PROJECT",
                    ["CorrelationId"] = $"DEMO-PROJECT:SLA:{reviewKey}",
                    ["DocumentId"] = DocumentId,
                    ["DocumentRevisionId"] = RevisionId,
                    ["DocumentNumber"] = DocumentNumber,
                    ["Revision"] = Revision,
                    ["ReviewCycleId"] = $"SLA:{reviewKey}",
                    ["DefinitionVersionId"] = "EngineeringReviewV1",
                    ["InitialDelay"] = initialDelay,
                    ["ReminderDelay"] = reminderDelay,
                    ["ReviewKey"] = reviewKey
                },
                IncludeWorkflowOutput = true
            });
            var state = await client.ExportStateAsync();
            return new SlaRun(state, response.SubStatus);
        }

        private async Task<(WorkflowState WorkflowState, WorkflowSubStatus SubStatus)> StartAsync<TWorkflow>(
            string versionId,
            string revisionId,
            int revision,
            string reviewCycleId,
            string documentId,
            string documentNumber)
            where TWorkflow : WorkflowBase, new()
        {
            var services = _provider;
            var workflow = await services.GetRequiredService<IWorkflowBuilderFactory>()
                .CreateBuilder().BuildWorkflowAsync<TWorkflow>();
            await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
            var graph = await services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
            var result = await services.GetRequiredService<IWorkflowRunner>().RunAsync(
                graph,
                CreateOptions(versionId, revisionId, revision, reviewCycleId, documentId, documentNumber));
            return (result.WorkflowState, result.WorkflowExecutionContext.SubStatus);
        }

        public async Task<IReadOnlyList<LoadRun>> StartBatchAsync(int count)
        {
            var workflow = await _provider.GetRequiredService<IWorkflowBuilderFactory>()
                .CreateBuilder().BuildWorkflowAsync<EdmsEngineeringReviewV1Workflow>();
            await _provider.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
            var graph = await _provider.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
            var runner = _provider.GetRequiredService<IWorkflowRunner>();
            var runs = new List<LoadRun>(count);
            for (var index = 0; index < count; index++)
            {
                var documentId = $"CAPSTONE-LOAD-{index:D3}";
                var documentNumber = $"DPC-90-PR-{index:D4}";
                var revisionId = $"{documentId}:R2";
                var cycleId = $"{revisionId}:C1";
                AddIncomingRevision(revisionId, Revision, $"Load fixture file {index}.", documentId, documentNumber);
                var result = await runner.RunAsync(graph, CreateOptions(
                    EdmsCapstoneDefinitionIdentity.Version1Id,
                    revisionId,
                    Revision,
                    cycleId,
                    documentId,
                    documentNumber));
                runs.Add(new LoadRun(result.WorkflowState, result.WorkflowExecutionContext.SubStatus, documentId, revisionId, cycleId));
            }
            return runs;
        }

        public void AddIncomingRevision(
            string revisionId,
            int revision,
            string content,
            string documentId = DocumentId,
            string documentNumber = DocumentNumber)
        {
            var path = Path.Combine(RootPath, "incoming", $"{documentNumber}-R{revision}.pdf");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            _documentRepository.RegisterIncoming(new DocumentMetadata(
                revisionId, documentNumber, revision, DocumentStorageLocation.Incoming, path, "Incoming", null));
        }

        public async Task<WorkflowState> ExportStateAsync(string workflowInstanceId) => await (await _provider
            .GetRequiredService<IWorkflowRuntime>().CreateClientAsync(workflowInstanceId)).ExportStateAsync();

        private static RunWorkflowOptions CreateOptions(
            string versionId,
            string revisionId,
            int revision,
            string reviewCycleId,
            string documentId,
            string documentNumber) => new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["ProjectId"] = "DEMO-PROJECT",
                ["CorrelationId"] = $"DEMO-PROJECT:{revisionId}:{reviewCycleId}",
                ["DocumentId"] = documentId,
                ["DocumentRevisionId"] = revisionId,
                ["DocumentNumber"] = documentNumber,
                ["Revision"] = revision,
                ["ReviewCycleId"] = reviewCycleId,
                ["DefinitionVersionId"] = versionId
            }
        }.WithCounterBasedFlowchart();

        public void Dispose()
        {
            _provider.Dispose();
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }

        public sealed record SlaRun(WorkflowState WorkflowState, WorkflowSubStatus SubStatus);
        public sealed record LoadRun(
            WorkflowState WorkflowState,
            WorkflowSubStatus SubStatus,
            string DocumentId,
            string DocumentRevisionId,
            string ReviewCycleId);
    }

    private sealed class CapstonePublicationService : IDocumentPublicationService
    {
        private readonly Dictionary<string, DocumentPublicationCommand> _applied = new(StringComparer.Ordinal);
        public int CallCount { get; private set; }
        public IReadOnlyCollection<DocumentPublicationCommand> AppliedOperations => _applied.Values.ToArray();

        public Task<DocumentPublicationResult> PublishAsync(DocumentPublicationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (_applied.TryGetValue(command.OperationId, out var existing))
            {
                if (existing != command)
                    throw new InvalidOperationException("The capstone publication operation ID was reused for another command.");
                return Task.FromResult(new DocumentPublicationResult(command.OperationId, DocumentPublicationStatus.AlreadyApplied,
                    command.DocumentNumber, command.Revision, command.Destination));
            }

            _applied.Add(command.OperationId, command);
            if (CallCount == 1)
                throw new TransientDocumentServiceException("publication applied but acknowledgement was lost");

            return Task.FromResult(new DocumentPublicationResult(command.OperationId, DocumentPublicationStatus.AlreadyApplied,
                command.DocumentNumber, command.Revision, command.Destination));
        }
    }
}
