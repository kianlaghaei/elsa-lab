using System.Text.Json;
using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Resilience;
using Elsa.Resilience.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class FailureProcessHarness
{
    private const string JsonMessagePrefix = "ELSA_PROCESS_RESULT=";

    public static async Task<int> RunAsync(string connectionString, string[] args) => args[0] switch
    {
        "failure-fault" => await FaultAsync(connectionString),
        "failure-inspect" when args.Length == 2 => await InspectAsync(connectionString, args[1]),
        _ => throw new ArgumentException($"Invalid failure harness command '{args[0]}'.")
    };

    private static async Task<int> FaultAsync(string connectionString)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        var workflow = await host.Services.GetRequiredService<IWorkflowBuilderFactory>()
            .CreateBuilder().BuildWorkflowAsync<DocumentPublicationWorkflow>();
        await host.Services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        var graph = await host.Services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
        var result = await host.Services.GetRequiredService<IWorkflowRunner>().RunAsync(
            graph,
            new RunWorkflowOptions
            {
                Input = new Dictionary<string, object>
                {
                    ["DocumentNumber"] = "DPC-10-ME-0001",
                    ["Revision"] = 2,
                    ["Destination"] = "Client"
                }
            });
        var incident = result.WorkflowState.Incidents.Single();
        var activity = result.Journal.ActivityExecutionContexts.Single(context => context.Activity is PublishDocumentActivity);
        WriteMessage(new
        {
            command = "failure-fault",
            processId = Environment.ProcessId,
            workflowInstanceId = result.WorkflowState.Id,
            status = result.WorkflowState.Status.ToString(),
            subStatus = result.WorkflowExecutionContext.SubStatus.ToString(),
            activityId = incident.ActivityId,
            activityExecutionId = activity.Id,
            activityStatus = activity.Status.ToString(),
            incidentMessage = incident.Message,
            incidentExceptionType = incident.Exception?.Type.FullName,
            downstreamExecuted = result.Journal.ActivityExecutionContexts.Any(context => context.Activity.Name == "AfterPublication"),
            incidentCount = result.WorkflowState.Incidents.Count
        });
        return 0;
    }

    private static async Task<int> InspectAsync(string connectionString, string workflowInstanceId)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        var instance = await host.Services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
            ?? throw new InvalidOperationException($"Workflow instance '{workflowInstanceId}' was not found in SQL.");
        var incident = instance.WorkflowState.Incidents.Single();
        WriteMessage(new
        {
            command = "failure-inspect",
            processId = Environment.ProcessId,
            workflowInstanceId = instance.Id,
            status = instance.Status.ToString(),
            subStatus = instance.SubStatus.ToString(),
            activityId = incident.ActivityId,
            incidentMessage = incident.Message,
            incidentExceptionType = incident.Exception?.Type.FullName,
            incidentExceptionMessage = incident.Exception?.Message,
            incidentCount = instance.WorkflowState.Incidents.Count,
            bookmarkCount = instance.WorkflowState.Bookmarks.Count
        });
        return 0;
    }

    private static async Task<IHost> CreateAndStartHostAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:Strategies:0:$type"] = nameof(DocumentPublicationRetryStrategy),
            ["Resilience:Strategies:0:Id"] = "document-publication",
            ["Resilience:Strategies:0:DisplayName"] = "Document publication transient retry",
            ["Resilience:Strategies:0:MaxRetryAttempts"] = "2",
            ["Resilience:Strategies:0:Delay"] = "00:00:00.005"
        });
        builder.Services.AddSingleton<IDocumentPublicationService, PermanentlyFailingPublicationService>();
        builder.Services.AddSingleton<ITransientExceptionStrategy, DocumentServiceTransientExceptionStrategy>();
        builder.Services.AddElsa(elsa =>
        {
            elsa.UseWorkflowManagement(management => management.UseEntityFrameworkCore(ef =>
            {
                ef.UseSqlServer(connectionString);
                ef.RunMigrations = true;
            }));
            elsa.UseWorkflowRuntime(runtime => runtime.UseEntityFrameworkCore(ef =>
            {
                ef.UseSqlServer(connectionString);
                ef.RunMigrations = true;
            }));
            elsa.UseResilience(resilience => resilience.AddResilienceStrategyType<DocumentPublicationRetryStrategy>());
            elsa.AddActivity<PublishDocumentActivity>();
            elsa.AddWorkflow<DocumentPublicationWorkflow>();
        });

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static void WriteMessage(object message) =>
        Console.WriteLine(JsonMessagePrefix + JsonSerializer.Serialize(message));

    private sealed class PermanentlyFailingPublicationService : IDocumentPublicationService
    {
        public Task<DocumentPublicationResult> PublishAsync(
            DocumentPublicationCommand command,
            CancellationToken cancellationToken) =>
            Task.FromException<DocumentPublicationResult>(
                new DocumentValidationException("The revision is not eligible for publication."));
    }
}
