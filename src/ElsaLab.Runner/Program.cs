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

const string documentId = "DOC-100";
const string documentNumber = "DPC-10-ME-0001";
const int revision = 2;
const string fileContents = "Sample document payload for the EDMS storage fit test.";
var rootPath = Path.Combine(Path.GetTempPath(), $"elsalab-edms-fit-{Guid.NewGuid():N}");
var incomingDirectory = Path.Combine(rootPath, "incoming");
var sourcePath = Path.Combine(incomingDirectory, $"{documentNumber}-R{revision}.pdf");
var destinationPath = Path.Combine(rootPath, "approved", $"{documentNumber}-R{revision}.pdf");

Directory.CreateDirectory(incomingDirectory);
await File.WriteAllTextAsync(sourcePath, fileContents);

var repository = new InMemoryDocumentRepository();
repository.RegisterIncoming(new DocumentMetadata(
    documentId,
    documentNumber,
    revision,
    DocumentStorageLocation.Incoming,
    sourcePath,
    "Incoming",
    null));

var services = new ServiceCollection();
services.AddSingleton(repository);
services.AddSingleton(new DocumentStorageOptions(rootPath));
services.AddSingleton<IDocumentStorageService, FileSystemDocumentStorageService>();
services.AddElsa(elsa =>
{
    elsa.AddActivity<MoveDocumentActivity>();
    elsa.AddWorkflow<DocumentStorageWorkflow>();
});

var exitCode = 1;
try
{
    using var serviceProvider = services.BuildServiceProvider();
    using var scope = serviceProvider.CreateScope();
    var workflowRunner = scope.ServiceProvider.GetRequiredService<IWorkflowRunner>();
    Console.WriteLine("EDMS document storage fit test (physical move adapter)");
    Console.WriteLine($"DocumentId={documentId}, DocumentNumber={documentNumber}, Revision={revision}, ReviewDecision=Approved");
    Console.WriteLine($"Source existed before workflow: {File.Exists(sourcePath)}");

    var result = await workflowRunner.RunAsync(
        new DocumentStorageWorkflow(),
        new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["DocumentId"] = documentId,
                ["DocumentNumber"] = documentNumber,
                ["Revision"] = revision,
                ["ReviewDecision"] = "Approved"
            }
        }.WithCounterBasedFlowchart());

    foreach (var context in result.Journal.ActivityExecutionContexts.OrderBy(context => context.StartedAt))
    {
        var name = string.IsNullOrWhiteSpace(context.Activity.Name) ? "<workflow-root>" : context.Activity.Name;
        Console.WriteLine($"Activity {name}: {context.Status}; execution={context.Id}; activity={context.Activity.Id}");
    }

    var decisionContext = result.Journal.ActivityExecutionContexts.Single(context => context.Activity is FlowDecision);
    var outcomes = string.Join(", ", (string[])decisionContext.JournalData["Outcomes"]);
    var metadata = repository.GetDocument(documentId);
    var operation = repository.GetOperations().SingleOrDefault();
    var attempt = repository.GetAttempts().SingleOrDefault();

    Console.WriteLine($"Selected decision outcome: {outcomes}");
    Console.WriteLine($"Storage operation: {attempt?.Command.OperationId} ({attempt?.OperationStatus})");
    Console.WriteLine($"Audit: workflow={attempt?.Command.WorkflowInstanceId}, activity={attempt?.Command.ActivityName}/{attempt?.Command.ActivityId}, execution={attempt?.Command.ActivityExecutionId}");
    Console.WriteLine($"Storage location: {metadata.LogicalLocation}; metadata status: {metadata.Status}");
    Console.WriteLine($"Workflow outputs: location={result.WorkflowState.Output["FinalLocation"]}, status={result.WorkflowState.Output["OperationStatus"]}, path={result.WorkflowState.Output["FinalPath"]}");
    Console.WriteLine($"Source exists after workflow: {File.Exists(sourcePath)}");
    Console.WriteLine($"Destination exists after workflow: {File.Exists(destinationPath)}");
    Console.WriteLine($"Destination content preserved: {File.Exists(destinationPath) && await File.ReadAllTextAsync(destinationPath) == fileContents}");
    Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
    Console.WriteLine($"Workflow substatus: {result.WorkflowExecutionContext.SubStatus}");

    var hasFaults = result.Journal.ActivityExecutionContexts.Any(context => context.Status == ActivityStatus.Faulted);
    exitCode = result.WorkflowState.Status == WorkflowStatus.Finished &&
               result.WorkflowExecutionContext.SubStatus == WorkflowSubStatus.Finished &&
               !hasFaults &&
               operation is not null &&
               attempt?.OperationStatus == DocumentStorageOperationStatus.Applied &&
               !File.Exists(sourcePath) &&
               File.Exists(destinationPath) &&
               metadata.LogicalLocation == DocumentStorageLocation.Approved &&
               string.Equals(await File.ReadAllTextAsync(destinationPath), fileContents, StringComparison.Ordinal)
        ? 0
        : 1;
}
finally
{
    if (Directory.Exists(rootPath))
        Directory.Delete(rootPath, recursive: true);
}

return exitCode;
