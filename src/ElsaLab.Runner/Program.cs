using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddElsa(elsa =>
{
    elsa.AddActivity<ReviewDocumentActivity>();
    elsa.AddWorkflow<DocumentRevisionWorkflow>();
});

using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
Console.WriteLine("Document revision cycle (Elsa token-based Flowchart)");
Console.WriteLine("DocumentNumber=DPC-10-ME-0001, InitialRevision=0, CommentRoundsBeforeApproval=2");
var result = await workflowRunner.RunAsync(
    new DocumentRevisionWorkflow(),
    new RunWorkflowOptions
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = "DPC-10-ME-0001",
            ["InitialRevision"] = 0,
            ["CommentRoundsBeforeApproval"] = 2
        }
    }.WithTokenBasedFlowchart());

Console.WriteLine($"Final revision: {result.WorkflowState.Output["FinalRevision"]}");
Console.WriteLine($"Review rounds: {result.WorkflowState.Output["ReviewRounds"]}");
Console.WriteLine($"Processing status: {result.WorkflowState.Output["ProcessingStatus"]}");
Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
Console.WriteLine($"Workflow substatus: {result.WorkflowExecutionContext.SubStatus}");
Console.WriteLine($"Typed workflow result: {result.Result}");

return result.WorkflowState.Status == WorkflowStatus.Finished &&
       result.WorkflowExecutionContext.SubStatus == WorkflowSubStatus.Finished
    ? 0
    : 1;
