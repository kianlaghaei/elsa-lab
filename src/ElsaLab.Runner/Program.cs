using Elsa.Extensions;
using Elsa.Workflows;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddElsa(elsa => elsa.AddWorkflow<DocumentReceivedWorkflow>());

using var serviceProvider = services.BuildServiceProvider();
var workflowRunner = serviceProvider.GetRequiredService<IWorkflowRunner>();
var result = await workflowRunner.RunAsync<DocumentReceivedWorkflow>();

Console.WriteLine($"Workflow status: {result.WorkflowState.Status}");
return result.WorkflowState.Status == WorkflowStatus.Finished ? 0 : 1;
