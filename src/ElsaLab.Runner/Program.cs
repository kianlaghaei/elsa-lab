using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddElsa(elsa => elsa.AddWorkflow<DocumentProcessingWorkflow>());

using var serviceProvider = services.BuildServiceProvider();
var workflowRunner = serviceProvider.GetRequiredService<IWorkflowRunner>();
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
    });

Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
Console.WriteLine($"Workflow result: {result.Result}");
Console.WriteLine($"Workflow output: ProcessingStatus={result.WorkflowState.Output["ProcessingStatus"]}");

var finalActivityContext = result.Journal.ActivityExecutionContexts.Last();
var finalExpressionContext = finalActivityContext.ExpressionExecutionContext;
Console.WriteLine(
    $"Final workflow variables: ProcessingStatus={finalExpressionContext.GetVariable<string>("ProcessingStatus")}, " +
    $"StepCount={finalExpressionContext.GetVariable<int>("StepCount")}");

return result.WorkflowState.Status == WorkflowStatus.Finished ? 0 : 1;
