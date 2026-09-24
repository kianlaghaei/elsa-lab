using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

const string documentNumber = "DPC-10-ME-0001";
const int revision = 2;
const string reviewKey = "discipline-review";

var services = new ServiceCollection();
services.AddElsa(elsa =>
{
    elsa.AddActivity<WaitForDocumentReviewActivity>();
    elsa.AddWorkflow<DocumentReviewBlockingWorkflow>();
});

using var serviceProvider = services.BuildServiceProvider();
using var scope = serviceProvider.CreateScope();
var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
Console.WriteLine("Document review blocking demonstration (in-memory Elsa bookmark)");
Console.WriteLine($"DocumentNumber={documentNumber}, Revision={revision}, ReviewKey={reviewKey}");

var result = await workflowRunner.RunAsync(
    new DocumentReviewBlockingWorkflow(),
    new RunWorkflowOptions
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = documentNumber,
            ["Revision"] = revision,
            ["ReviewKey"] = reviewKey
        }
    });

foreach (var context in result.Journal.ActivityExecutionContexts.OrderBy(context => context.StartedAt))
{
    var activityName = string.IsNullOrEmpty(context.Activity.Name) ? "<workflow-root>" : context.Activity.Name;
    var schedulingExecutionId = context.SchedulingActivityExecutionId ?? "none";
    Console.WriteLine(
        $"Activity {activityName}: {context.Status}; execution={context.Id}; activity={context.Activity.Id}; " +
        $"node={context.Activity.NodeId}; scheduledBy={schedulingExecutionId}; executionCount={context.ExecutionCount}; " +
        $"journalDataKeys=[{string.Join(", ", context.JournalData.Keys)}]");
}

foreach (var bookmark in result.WorkflowState.Bookmarks)
{
    var payload = (DocumentReviewBookmarkPayload)bookmark.Payload!;
    Console.WriteLine(
        $"Bookmark {bookmark.Name} ({bookmark.Id}): {payload.DocumentNumber}, revision {payload.Revision}, key {payload.ReviewKey}");
}

var finalized = result.Journal.ActivityExecutionContexts.Any(context => context.Activity.Name == "FinalizeDocument");
Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
Console.WriteLine($"Workflow substatus: {result.WorkflowExecutionContext.SubStatus}");
Console.WriteLine($"FinalizeDocument executed: {finalized}");

return result.WorkflowState.Status == WorkflowStatus.Running &&
       result.WorkflowExecutionContext.SubStatus == WorkflowSubStatus.Suspended &&
       result.WorkflowState.Bookmarks.Count == 1 &&
       !finalized
    ? 0
    : 1;
