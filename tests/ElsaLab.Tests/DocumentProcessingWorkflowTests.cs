using Elsa.Extensions;
using Elsa.Workflows;
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
    public async Task DocumentProcessingWorkflow_UsesRegisteredService_AndFinishes()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
        AddWorkflowServices(services);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await workflowRunner.RunAsync(
            new DocumentProcessingWorkflow(),
            new RunWorkflowOptions { Input = CreateInput() });

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);
        Assert.Equal("REG-DPC-10-ME-0001-R2", result.Result);
        Assert.Equal(true, result.WorkflowState.Output["IsValid"]);
        Assert.Equal("Registered DPC-10-ME-0001, revision 2.", result.WorkflowState.Output["ProcessingMessage"]);
        Assert.Equal("REG-DPC-10-ME-0001-R2", result.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal(DocumentNumber, result.WorkflowExecutionContext.Input["DocumentNumber"]);
        Assert.Equal(Revision, result.WorkflowExecutionContext.Input["Revision"]);

        var registrationContext = result.Journal.ActivityExecutionContexts.Single(context => context.Activity is RegisterDocumentActivity);
        Assert.Equal(ActivityStatus.Completed, registrationContext.Status);
        Assert.Equal(DocumentNumber, registrationContext.GetInputs()["DocumentNumber"]);
        Assert.Equal(Revision, registrationContext.GetInputs()["Revision"]);
        Assert.Equal(true, registrationContext.GetOutputs()["IsValid"]);
        Assert.Equal("Registered DPC-10-ME-0001, revision 2.", registrationContext.GetOutputs()["ProcessingMessage"]);
        Assert.Equal("REG-DPC-10-ME-0001-R2", registrationContext.GetOutputs()["RegistrationReference"]);
    }

    [Fact]
    public async Task DocumentProcessingWorkflow_UsesFakeServiceThroughDi_AndPassesOutputToLaterSteps()
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
            new RunWorkflowOptions { Input = CreateInput() },
            cancellationToken);

        Assert.Equal(1, fakeService.CallCount);
        Assert.Equal(DocumentNumber, fakeService.ReceivedDocumentNumber);
        Assert.Equal(Revision, fakeService.ReceivedRevision);
        Assert.Equal(cancellationToken, fakeService.ReceivedCancellationToken);

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);
        Assert.Equal("FAKE-REG-42", result.Result);
        Assert.Equal(true, result.WorkflowState.Output["IsValid"]);
        Assert.Equal("Fake service accepted the document.", result.WorkflowState.Output["ProcessingMessage"]);
        Assert.Equal("FAKE-REG-42", result.WorkflowState.Output["RegistrationReference"]);

        var registrationContext = result.Journal.ActivityExecutionContexts.Single(context => context.Activity is RegisterDocumentActivity);
        Assert.Equal("FAKE-REG-42", registrationContext.GetOutputs()["RegistrationReference"]);

        var downstreamContext = result.Journal.ActivityExecutionContexts.Last();
        var downstreamVariables = downstreamContext.ExpressionExecutionContext;
        Assert.Equal("FAKE-REG-42", downstreamVariables.GetVariable<string>("RegistrationReference"));
        Assert.Equal("Fake service accepted the document.", downstreamVariables.GetVariable<string>("ProcessingMessage"));
        Assert.True(downstreamVariables.GetVariable<bool>("IsValid"));
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
            new RunWorkflowOptions { Input = CreateInput(DocumentNumber) });
        var secondResult = await workflowRunner.RunAsync(
            workflowGraph,
            new RunWorkflowOptions { Input = CreateInput("DPC-10-ME-0002") });

        var firstActivity = firstResult.Journal.ActivityExecutionContexts
            .Single(context => context.Activity is RegisterDocumentActivity).Activity;
        var secondActivity = secondResult.Journal.ActivityExecutionContexts
            .Single(context => context.Activity is RegisterDocumentActivity).Activity;

        Assert.Same(firstActivity, secondActivity);
        Assert.Equal("REG-DPC-10-ME-0001-R2", firstResult.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal("REG-DPC-10-ME-0002-R2", secondResult.WorkflowState.Output["RegistrationReference"]);
        Assert.Equal(WorkflowSubStatus.Finished, firstResult.WorkflowExecutionContext.SubStatus);
        Assert.Equal(WorkflowSubStatus.Finished, secondResult.WorkflowExecutionContext.SubStatus);
    }

    private static void AddWorkflowServices(IServiceCollection services)
    {
        services.AddElsa(elsa =>
        {
            elsa.AddActivity<RegisterDocumentActivity>();
            elsa.AddWorkflow<DocumentProcessingWorkflow>();
        });
    }

    private static IDictionary<string, object> CreateInput(string documentNumber = DocumentNumber) => new Dictionary<string, object>
    {
        ["DocumentNumber"] = documentNumber,
        ["Revision"] = Revision
    };

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
