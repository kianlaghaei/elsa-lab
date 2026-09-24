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

public class DocumentProcessingWorkflowTests
{
    private const string DocumentNumber = "DPC-10-ME-0001";
    private const int Revision = 2;

    [Fact]
    public async Task RequiresReviewTrue_RoutesToReviewAndRejoinsCompletion()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
        AddWorkflowServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await workflowRunner.RunAsync(
            new DocumentProcessingWorkflow(),
            CreateOptions(DocumentNumber, requiresReview: true));

        AssertSuccessfulBranch(result, "True", "ReviewDocument", "AutoAcceptDocument");
        Assert.Equal("Reviewed", result.Result);
        Assert.Equal(true, result.WorkflowState.Output["IsValid"]);
        Assert.Equal("Registered DPC-10-ME-0001, revision 2.", result.WorkflowState.Output["ProcessingMessage"]);
        Assert.Equal("Reviewed", result.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal("REG-DPC-10-ME-0001-R2", result.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal(true, result.WorkflowExecutionContext.Input["RequiresReview"]);

        var registrationContext = result.Journal.ActivityExecutionContexts
            .Single(context => context.Activity is RegisterDocumentActivity);
        Assert.Equal(DocumentNumber, registrationContext.GetInputs()["DocumentNumber"]);
        Assert.Equal(Revision, registrationContext.GetInputs()["Revision"]);
        Assert.Equal(true, registrationContext.GetOutputs()["IsValid"]);
        Assert.Equal("Registered DPC-10-ME-0001, revision 2.", registrationContext.GetOutputs()["ProcessingMessage"]);
        Assert.Equal("REG-DPC-10-ME-0001-R2", registrationContext.GetOutputs()["RegistrationReference"]);
    }

    [Fact]
    public async Task RequiresReviewFalse_RoutesToFastPathAndRejoinsCompletion()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
        AddWorkflowServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await workflowRunner.RunAsync(
            new DocumentProcessingWorkflow(),
            CreateOptions(DocumentNumber, requiresReview: false));

        AssertSuccessfulBranch(result, "False", "AutoAcceptDocument", "ReviewDocument");
        Assert.Equal("AutoAccepted", result.Result);
        Assert.Equal(true, result.WorkflowState.Output["IsValid"]);
        Assert.Equal("Registered DPC-10-ME-0001, revision 2.", result.WorkflowState.Output["ProcessingMessage"]);
        Assert.Equal("AutoAccepted", result.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal("REG-DPC-10-ME-0001-R2", result.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal(false, result.WorkflowExecutionContext.Input["RequiresReview"]);
    }

    [Fact]
    public async Task FlowchartUsesFakeServiceThroughDi_AndPropagatesCancellation()
    {
        var expected = new DocumentRegistrationResult(true, "Fake service accepted the document.", "FAKE-REG-42");
        var fakeService = new RecordingDocumentProcessingService(expected);
        var services = new ServiceCollection();
        services.AddScoped<IDocumentProcessingService>(_ => fakeService);
        AddWorkflowServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
        using var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var result = await workflowRunner.RunAsync(
            new DocumentProcessingWorkflow(),
            CreateOptions(DocumentNumber, requiresReview: true),
            cancellationToken);

        Assert.Equal(1, fakeService.CallCount);
        Assert.Equal(DocumentNumber, fakeService.ReceivedDocumentNumber);
        Assert.Equal(Revision, fakeService.ReceivedRevision);
        Assert.Equal(cancellationToken, fakeService.ReceivedCancellationToken);
        AssertSuccessfulBranch(result, "True", "ReviewDocument", "AutoAcceptDocument");
        Assert.Equal("Reviewed", result.Result);
        Assert.Equal("FAKE-REG-42", result.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal("Fake service accepted the document.", result.Journal.ActivityExecutionContexts
            .Single(context => context.Activity is RegisterDocumentActivity)
            .GetOutputs()["ProcessingMessage"]);

        var lastActivityContext = result.Journal.ActivityExecutionContexts.Last();
        Assert.Equal("FAKE-REG-42", lastActivityContext.ExpressionExecutionContext.GetVariable<string>("RegistrationReference"));
        Assert.Equal("Fake service accepted the document.", lastActivityContext.ExpressionExecutionContext.GetVariable<string>("ProcessingMessage"));
    }

    [Fact]
    public async Task ReusingWorkflowGraph_ReusesActivityDefinitionInstance_AndKeepsRunDataSeparate()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
        AddWorkflowServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var scopedServices = scope.ServiceProvider;
        var workflowRunner = scopedServices.GetRequiredService<IWorkflowRunner>();
        var workflowBuilder = scopedServices.GetRequiredService<IWorkflowBuilderFactory>().CreateBuilder();
        var workflow = await workflowBuilder.BuildWorkflowAsync<DocumentProcessingWorkflow>();
        var workflowGraph = await scopedServices.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);

        var firstResult = await workflowRunner.RunAsync(
            workflowGraph,
            CreateOptions(DocumentNumber, requiresReview: true));
        var secondResult = await workflowRunner.RunAsync(
            workflowGraph,
            CreateOptions("DPC-10-ME-0002", requiresReview: false));

        var firstActivity = firstResult.Journal.ActivityExecutionContexts
            .Single(context => context.Activity is RegisterDocumentActivity).Activity;
        var secondActivity = secondResult.Journal.ActivityExecutionContexts
            .Single(context => context.Activity is RegisterDocumentActivity).Activity;

        Assert.Same(firstActivity, secondActivity);
        Assert.Equal("REG-DPC-10-ME-0001-R2", firstResult.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal("REG-DPC-10-ME-0002-R2", secondResult.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal("Reviewed", firstResult.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal("AutoAccepted", secondResult.WorkflowState.Output["ProcessingStatus"]);
        Assert.Equal(WorkflowSubStatus.Finished, firstResult.WorkflowExecutionContext.SubStatus);
        Assert.Equal(WorkflowSubStatus.Finished, secondResult.WorkflowExecutionContext.SubStatus);
    }

    private static void AssertSuccessfulBranch(
        RunWorkflowResult<string> result,
        string expectedOutcome,
        string expectedBranch,
        string skippedBranch)
    {
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);

        var contexts = result.Journal.ActivityExecutionContexts;
        Assert.DoesNotContain(contexts, context => context.Status == ActivityStatus.Faulted);

        var flowchartContext = contexts.Single(context => context.Activity is Flowchart);
        Assert.Equal(ActivityStatus.Completed, flowchartContext.Status);

        var decisionContext = contexts.Single(context => context.Activity is FlowDecision);
        Assert.Equal(new[] { expectedOutcome }, Assert.IsType<string[]>(decisionContext.JournalData["Outcomes"]));

        Assert.Contains(contexts, context => context.Activity.Name == expectedBranch);
        Assert.DoesNotContain(contexts, context => context.Activity.Name == skippedBranch);
        Assert.Single(contexts, context => context.Activity.Name == "CompleteDocument");
    }

    private static RunWorkflowOptions CreateOptions(string documentNumber, bool requiresReview) =>
        new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["DocumentNumber"] = documentNumber,
                ["Revision"] = Revision,
                ["RequiresReview"] = requiresReview
            }
        }.WithCounterBasedFlowchart();

    private static void AddWorkflowServices(IServiceCollection services)
    {
        services.AddElsa(elsa =>
        {
            elsa.AddActivity<RegisterDocumentActivity>();
            elsa.AddWorkflow<DocumentProcessingWorkflow>();
        });
    }

    private sealed class RecordingDocumentProcessingService(DocumentRegistrationResult result) : IDocumentProcessingService
    {
        public int CallCount { get; private set; }
        public string? ReceivedDocumentNumber { get; private set; }
        public int ReceivedRevision { get; private set; }
        public CancellationToken ReceivedCancellationToken { get; private set; }

        public Task<DocumentRegistrationResult> RegisterAsync(
            string documentNumber,
            int revision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ReceivedDocumentNumber = documentNumber;
            ReceivedRevision = revision;
            ReceivedCancellationToken = cancellationToken;
            return Task.FromResult(result);
        }
    }
}
