using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public sealed class EdmsDocumentStorageWorkflowTests
{
    private const string DocumentId = "DOC-100";
    private const string DocumentNumber = "DPC-10-ME-0001";
    private const int Revision = 2;
    private const string Content = "Deterministic document payload for EDMS-FIT-01.";

    [Fact]
    public async Task ApprovedWorkflow_MovesDocumentAndPublishesMetadataAndAudit()
    {
        using var environment = new StorageEnvironment();
        var sourcePath = environment.SeedIncomingDocument(DocumentId, DocumentNumber, Revision, Content);
        Assert.True(File.Exists(sourcePath));

        using var cancellationSource = new CancellationTokenSource();
        var result = await environment.RunAsync("Approved", cancellationSource.Token);

        var destinationPath = Path.Combine(environment.RootPath, "approved", $"{DocumentNumber}-R{Revision}.pdf");
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(Content, await File.ReadAllTextAsync(destinationPath));

        var metadata = environment.Repository.GetDocument(DocumentId);
        Assert.Equal(DocumentStorageLocation.Approved, metadata.LogicalLocation);
        Assert.Equal("Approved", metadata.Status);
        Assert.Equal(destinationPath, metadata.PhysicalPath);
        Assert.Equal($"Move:{DocumentNumber}:R{Revision}:Approved", metadata.LastStorageOperationId);

        AssertSuccessfulRoute(result, "True", "MoveDocumentApproved", "MoveDocumentRevisionRequired");
        Assert.Equal("Approved", result.WorkflowState.Output["FinalLocation"]);
        Assert.Equal("Applied", result.WorkflowState.Output["OperationStatus"]);
        Assert.Equal(destinationPath, result.WorkflowState.Output["FinalPath"]);
        Assert.Equal(metadata.LastStorageOperationId, result.WorkflowState.Output["OperationId"]);

        var invocation = Assert.Single(environment.StorageService.Invocations);
        Assert.Equal(cancellationSource.Token, invocation.CancellationToken);
        Assert.Equal(DocumentId, invocation.Command.DocumentId);
        Assert.Equal(DocumentNumber, invocation.Command.DocumentNumber);
        Assert.Equal(Revision, invocation.Command.Revision);
        Assert.Equal(DocumentStorageLocation.Incoming, invocation.Command.SourceLocation);
        Assert.Equal(DocumentStorageLocation.Approved, invocation.Command.DestinationLocation);
        Assert.Equal(result.WorkflowExecutionContext.Id, invocation.Command.WorkflowInstanceId);

        var moveContext = Assert.Single(result.Journal.ActivityExecutionContexts,
            context => context.Activity is MoveDocumentActivity);
        Assert.Equal(invocation.Command.ActivityId, moveContext.Activity.Id);
        Assert.Equal(invocation.Command.ActivityExecutionId, moveContext.Id);
        Assert.Equal(DocumentId, moveContext.GetInputs()[nameof(MoveDocumentActivity.DocumentId)]);
        Assert.Equal(DocumentNumber, moveContext.GetInputs()[nameof(MoveDocumentActivity.DocumentNumber)]);
        Assert.Equal(Revision, moveContext.GetInputs()[nameof(MoveDocumentActivity.Revision)]);
        Assert.Equal(DocumentStorageLocation.Incoming,
            moveContext.GetInputs()[nameof(MoveDocumentActivity.SourceLocation)]);
        Assert.Equal(DocumentStorageLocation.Approved,
            moveContext.GetInputs()[nameof(MoveDocumentActivity.DestinationLocation)]);
        Assert.Equal(ActivityStatus.Completed, moveContext.Status);
        Assert.Equal("Approved", moveContext.GetOutputs()[nameof(MoveDocumentActivity.FinalLocation)]);
        Assert.Equal("Applied", moveContext.GetOutputs()[nameof(MoveDocumentActivity.OperationStatus)]);
        Assert.Single(environment.Repository.GetOperations());
        Assert.Equal(DocumentStorageOperationStatus.Applied,
            Assert.Single(environment.Repository.GetAttempts()).OperationStatus);
    }

    [Fact]
    public async Task RepeatingSameElsaWorkflowOperation_IsIdempotent_AndConflictingReuseIsRejected()
    {
        using var environment = new StorageEnvironment();
        var sourcePath = environment.SeedIncomingDocument(DocumentId, DocumentNumber, Revision, Content);
        var serviceScope = environment.Services.CreateScope();
        using (serviceScope)
        {
            var runner = serviceScope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
            var workflowBuilder = serviceScope.ServiceProvider.GetRequiredService<IWorkflowBuilderFactory>().CreateBuilder();
            var workflow = await workflowBuilder.BuildWorkflowAsync<DocumentStorageWorkflow>();
            var graph = await serviceScope.ServiceProvider.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
            var options = CreateOptions("Approved");

            var first = await runner.RunAsync(graph, options);
            var second = await runner.RunAsync(graph, options);

            AssertSuccessfulRoute(first, "True", "MoveDocumentApproved", "MoveDocumentRevisionRequired");
            AssertSuccessfulRoute(second, "True", "MoveDocumentApproved", "MoveDocumentRevisionRequired");
            Assert.Equal("Applied", first.WorkflowState.Output["OperationStatus"]);
            Assert.Equal("AlreadyApplied", second.WorkflowState.Output["OperationStatus"]);
            Assert.Equal(first.WorkflowState.Output["OperationId"], second.WorkflowState.Output["OperationId"]);
            Assert.NotEqual(first.WorkflowExecutionContext.Id, second.WorkflowExecutionContext.Id);

            var firstMove = Assert.Single(first.Journal.ActivityExecutionContexts,
                context => context.Activity is MoveDocumentActivity);
            var secondMove = Assert.Single(second.Journal.ActivityExecutionContexts,
                context => context.Activity is MoveDocumentActivity);
            Assert.Equal(firstMove.Activity.Id, secondMove.Activity.Id);
            Assert.Equal(firstMove.Activity.NodeId, secondMove.Activity.NodeId);
            Assert.NotEqual(firstMove.Id, secondMove.Id);
        }

        var destinationPath = Path.Combine(environment.RootPath, "approved", $"{DocumentNumber}-R{Revision}.pdf");
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(Content, await File.ReadAllTextAsync(destinationPath));

        var operations = Assert.Single(environment.Repository.GetOperations());
        Assert.Equal($"Move:{DocumentNumber}:R{Revision}:Approved", operations.OperationId);
        var attempts = environment.Repository.GetAttempts();
        Assert.Equal(2, attempts.Count);
        Assert.Equal(
            [DocumentStorageOperationStatus.Applied, DocumentStorageOperationStatus.AlreadyApplied],
            attempts.Select(attempt => attempt.OperationStatus).ToArray());
        Assert.Single(Directory.GetFiles(Path.Combine(environment.RootPath, "approved")));
        Assert.Equal(DocumentStorageLocation.Approved, environment.Repository.GetDocument(DocumentId).LogicalLocation);
        Assert.Equal(operations.OperationId, environment.Repository.GetDocument(DocumentId).LastStorageOperationId);

        var appliedCommand = attempts[0].Command;
        var conflictingCommand = appliedCommand with
        {
            DestinationLocation = DocumentStorageLocation.RevisionRequired
        };
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await environment.StorageService.MoveAsync(conflictingCommand, CancellationToken.None));

        Assert.Single(environment.Repository.GetOperations());
        Assert.Equal(2, environment.Repository.GetAttempts().Count);
        Assert.Equal(DocumentStorageLocation.Approved, environment.Repository.GetDocument(DocumentId).LogicalLocation);
        Assert.Equal(Content, await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(Path.Combine(environment.RootPath, "revision-required", $"{DocumentNumber}-R{Revision}.pdf")));
    }

    [Fact]
    public async Task NeedsRevisionDecision_MovesToRevisionRequired()
    {
        using var environment = new StorageEnvironment();
        var sourcePath = environment.SeedIncomingDocument(DocumentId, DocumentNumber, Revision, Content);

        var result = await environment.RunAsync("NeedsRevision");

        var destinationPath = Path.Combine(environment.RootPath, "revision-required", $"{DocumentNumber}-R{Revision}.pdf");
        AssertSuccessfulRoute(result, "False", "MoveDocumentRevisionRequired", "MoveDocumentApproved");
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(destinationPath));
        Assert.Equal(Content, await File.ReadAllTextAsync(destinationPath));
        Assert.Equal(DocumentStorageLocation.RevisionRequired, environment.Repository.GetDocument(DocumentId).LogicalLocation);
        Assert.Equal("RevisionRequired", result.WorkflowState.Output["FinalLocation"]);
        Assert.Equal("Applied", result.WorkflowState.Output["OperationStatus"]);
        Assert.Single(environment.Repository.GetOperations());
        Assert.Single(environment.StorageService.Invocations);
    }

    [Fact]
    public async Task DuplicateOperation_RejectsChangedDestinationContentsWithoutOverwriting()
    {
        using var environment = new StorageEnvironment();
        var sourcePath = environment.SeedIncomingDocument(DocumentId, DocumentNumber, Revision, Content);
        await environment.RunAsync("Approved");

        var destinationPath = Path.Combine(environment.RootPath, "approved", $"{DocumentNumber}-R{Revision}.pdf");
        const string unexpectedContents = "Unexpected content written after the original move.";
        await File.WriteAllTextAsync(destinationPath, unexpectedContents);

        var appliedCommand = Assert.Single(environment.Repository.GetAttempts()).Command;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await environment.StorageService.MoveAsync(appliedCommand, CancellationToken.None));

        Assert.False(File.Exists(sourcePath));
        Assert.Equal(unexpectedContents, await File.ReadAllTextAsync(destinationPath));
        Assert.Single(environment.Repository.GetOperations());
        Assert.Single(environment.Repository.GetAttempts());
        Assert.Equal(DocumentStorageLocation.Approved, environment.Repository.GetDocument(DocumentId).LogicalLocation);
    }

    [Fact]
    public async Task StorageService_RejectsPathLikeDocumentNumberWithoutChangingFile()
    {
        using var environment = new StorageEnvironment();
        var sourcePath = environment.SeedIncomingDocument(DocumentId, DocumentNumber, Revision, Content);
        var command = new DocumentStorageCommand(
            $"Move:{DocumentNumber}:R{Revision}:Approved",
            DocumentId,
            "../outside",
            Revision,
            DocumentStorageLocation.Incoming,
            DocumentStorageLocation.Approved,
            "workflow-test",
            "activity-test",
            "execution-test",
            "MoveDocumentApproved");

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await environment.StorageService.MoveAsync(command, CancellationToken.None));

        Assert.True(File.Exists(sourcePath));
        Assert.Equal(Content, await File.ReadAllTextAsync(sourcePath));
        Assert.Empty(environment.Repository.GetOperations());
        Assert.Empty(environment.Repository.GetAttempts());
    }

    private static void AssertSuccessfulRoute(
        RunWorkflowResult result,
        string expectedOutcome,
        string expectedActivity,
        string skippedActivity)
    {
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);

        var contexts = result.Journal.ActivityExecutionContexts;
        Assert.DoesNotContain(contexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Contains(contexts, context => context.Activity.Name == "DocumentStorageFlowchart" &&
                                                context.Status == ActivityStatus.Completed);
        Assert.Contains(contexts, context => context.Activity.Name == "CompleteDocument");
        Assert.Contains(contexts, context => context.Activity.Name == expectedActivity);
        Assert.DoesNotContain(contexts, context => context.Activity.Name == skippedActivity);

        var decisionContext = Assert.Single(contexts, context => context.Activity is FlowDecision);
        Assert.Equal(new[] { expectedOutcome }, Assert.IsType<string[]>(decisionContext.JournalData["Outcomes"]));
        Assert.Single(contexts, context => context.Activity is MoveDocumentActivity);
    }

    private static RunWorkflowOptions CreateOptions(string reviewDecision) =>
        new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["DocumentId"] = DocumentId,
                ["DocumentNumber"] = DocumentNumber,
                ["Revision"] = Revision,
                ["ReviewDecision"] = reviewDecision
            }
        }.WithCounterBasedFlowchart();

    private sealed class StorageEnvironment : IDisposable
    {
        private readonly string _incomingDirectory;

        public StorageEnvironment()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"elsalab-edms-fit-{Guid.NewGuid():N}");
            _incomingDirectory = Path.Combine(RootPath, "incoming");
            Directory.CreateDirectory(_incomingDirectory);

            Repository = new InMemoryDocumentRepository();
            var options = new DocumentStorageOptions(RootPath);
            var fileSystemService = new FileSystemDocumentStorageService(options, Repository);
            StorageService = new RecordingDocumentStorageService(fileSystemService);

            var services = new ServiceCollection();
            services.AddSingleton(Repository);
            services.AddSingleton(options);
            services.AddSingleton<IDocumentStorageService>(StorageService);
            services.AddElsa(elsa =>
            {
                elsa.AddActivity<MoveDocumentActivity>();
                elsa.AddWorkflow<DocumentStorageWorkflow>();
            });

            Services = services.BuildServiceProvider();
        }

        public string RootPath { get; }
        public InMemoryDocumentRepository Repository { get; }
        public RecordingDocumentStorageService StorageService { get; }
        public ServiceProvider Services { get; }

        public string SeedIncomingDocument(string documentId, string documentNumber, int revision, string contents)
        {
            var sourcePath = Path.Combine(_incomingDirectory, $"{documentNumber}-R{revision}.pdf");
            File.WriteAllText(sourcePath, contents);
            Repository.RegisterIncoming(new DocumentMetadata(
                documentId,
                documentNumber,
                revision,
                DocumentStorageLocation.Incoming,
                sourcePath,
                "Incoming",
                null));
            return sourcePath;
        }

        public async Task<RunWorkflowResult> RunAsync(string reviewDecision, CancellationToken cancellationToken = default)
        {
            using var scope = Services.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
            return await runner.RunAsync(
                new DocumentStorageWorkflow(),
                CreateOptions(reviewDecision),
                cancellationToken);
        }

        public void Dispose()
        {
            Services.Dispose();
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }

    private sealed class RecordingDocumentStorageService(IDocumentStorageService inner) : IDocumentStorageService
    {
        private readonly List<(DocumentStorageCommand Command, CancellationToken CancellationToken)> _invocations = [];

        public IReadOnlyList<(DocumentStorageCommand Command, CancellationToken CancellationToken)> Invocations =>
            _invocations.ToArray();

        public async Task<DocumentStorageResult> MoveAsync(
            DocumentStorageCommand command,
            CancellationToken cancellationToken)
        {
            _invocations.Add((command, cancellationToken));
            return await inner.MoveAsync(command, cancellationToken);
        }
    }
}
