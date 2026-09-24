using Elsa.Extensions;
using Elsa.Workflows;
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
            ["Revision"] = 2
        }
    });

Console.WriteLine("Caller inputs: DocumentNumber=DPC-10-ME-0001, Revision=2");
Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
Console.WriteLine($"Typed workflow result: RegistrationReference={result.Result}");
Console.WriteLine(
    $"Workflow outputs: IsValid={result.WorkflowState.Output["IsValid"]}, " +
    $"ProcessingMessage={result.WorkflowState.Output["ProcessingMessage"]}, " +
    $"RegistrationReference={result.WorkflowState.Output["RegistrationReference"]}");

return result.WorkflowState.Status == WorkflowStatus.Finished ? 0 : 1;
