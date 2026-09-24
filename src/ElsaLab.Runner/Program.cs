using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
services.AddElsa(elsa =>
{
    elsa.AddActivity<RegisterDocumentActivity>();
    elsa.AddWorkflow<DocumentProcessingWorkflow>();
});

using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
var result = await workflowRunner.RunAsync(
    new DocumentProcessingWorkflow(),
    new RunWorkflowOptions
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = "DPC-10-ME-0001",
            ["Revision"] = 2,
            ["RequiresReview"] = true
        }
    }.WithCounterBasedFlowchart());

Console.WriteLine("Caller inputs: DocumentNumber=DPC-10-ME-0001, Revision=2, RequiresReview=True");
Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
Console.WriteLine($"Workflow substatus: {result.WorkflowExecutionContext.SubStatus}");
Console.WriteLine($"Typed workflow result: ProcessingStatus={result.Result}");
Console.WriteLine(
    $"Workflow outputs: ProcessingStatus={result.WorkflowState.Output["ProcessingStatus"]}, " +
    $"RegistrationReference={result.WorkflowState.Output["RegistrationReference"]}");

return result.WorkflowState.Status == WorkflowStatus.Finished &&
       result.WorkflowExecutionContext.SubStatus == WorkflowSubStatus.Finished
    ? 0
    : 1;
