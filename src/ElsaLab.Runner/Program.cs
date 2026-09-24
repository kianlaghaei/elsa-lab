using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddScoped<IDocumentDisciplineReviewService, DocumentDisciplineReviewService>();
services.AddElsa(elsa =>
{
    elsa.AddActivity<ReviewDisciplineActivity>();
    elsa.AddActivity<ConsolidateReviewsActivity>();
    elsa.AddWorkflow<DocumentDisciplineReviewWorkflow>();
});

using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
Console.WriteLine("Document discipline review (Elsa token-based Flowchart; WaitAll join)");
Console.WriteLine("DocumentNumber=DPC-10-ME-0001");

var result = await workflowRunner.RunAsync(
    new DocumentDisciplineReviewWorkflow(),
    new RunWorkflowOptions
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = "DPC-10-ME-0001"
        }
    }.WithTokenBasedFlowchart());

Console.WriteLine("Post-run activity journal (execution order):");
foreach (var context in result.Journal.ActivityExecutionContexts.OrderBy(context => context.StartedAt))
{
    if (context.Activity is ReviewDisciplineActivity)
        Console.WriteLine($"{context.Activity.Name}: {context.GetOutputs()[nameof(ReviewDisciplineActivity.ReviewResult)]}");
    else if (context.Activity is FlowJoin)
        Console.WriteLine($"FlowJoin completed: {context.GetInputs()["Mode"]}");
    else if (context.Activity.Name == "ConsolidateReviews")
        Console.WriteLine($"ConsolidateReviews completed: {context.GetOutputs()[nameof(ConsolidateReviewsActivity.ConsolidatedStatus)]}");
}

Console.WriteLine($"Process review: {result.WorkflowState.Output["ProcessReview"]}");
Console.WriteLine($"Mechanical review: {result.WorkflowState.Output["MechanicalReview"]}");
Console.WriteLine($"Instrument review: {result.WorkflowState.Output["InstrumentReview"]}");
Console.WriteLine($"Consolidated status: {result.WorkflowState.Output["ConsolidatedStatus"]}");
Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
Console.WriteLine($"Workflow substatus: {result.WorkflowExecutionContext.SubStatus}");
Console.WriteLine($"Typed workflow result: {result.Result}");

return result.WorkflowState.Status == WorkflowStatus.Finished &&
       result.WorkflowExecutionContext.SubStatus == WorkflowSubStatus.Finished
    ? 0
    : 1;
