using System.Text.Json;
using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Resilience;
using Elsa.Resilience.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Activities;
using Elsa.Workflows.Runtime.Notifications;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Capstone;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class EdmsCapstoneProcessHarness
{
    private const string MessagePrefix = "EDMS_CAPSTONE_RESULT=";

    public static async Task<int> SuspendAndHoldAsync(string connectionString)
    {
        await SqlEdmsReviewTaskStore.EnsureSchemaAsync(connectionString);
        using var host = await CreateHostAsync(connectionString);
        var graph = await BuildV1GraphAsync(host.Services);
        var result = await host.Services.GetRequiredService<IWorkflowRunner>().RunAsync(graph, new RunWorkflowOptions
        {
            Input = new Dictionary<string, object>
            {
                ["ProjectId"] = "DEMO-PROJECT",
                ["CorrelationId"] = "DEMO-PROJECT:DPC-80-PR-0001:R2:C1",
                ["DocumentId"] = "CAPSTONE-RESTART-DOC-1",
                ["DocumentRevisionId"] = "CAPSTONE-RESTART-DOC-1:R2",
                ["DocumentNumber"] = "DPC-80-PR-0001",
                ["Revision"] = 2,
                ["ReviewCycleId"] = "CAPSTONE-RESTART-DOC-1:R2:C1",
                ["DefinitionVersionId"] = EdmsCapstoneDefinitionIdentity.Version1Id
            }
        }.WithCounterBasedFlowchart());

        var tasks = await host.Services.GetRequiredService<IEdmsReviewTaskStore>()
            .FindByWorkflowAsync(result.WorkflowState.Id, CancellationToken.None);
        if (result.WorkflowState.Status != WorkflowStatus.Running ||
            result.WorkflowExecutionContext.SubStatus != WorkflowSubStatus.Suspended ||
            result.WorkflowState.Bookmarks.Count != 3 || tasks.Count != 3 || result.WorkflowState.Incidents.Count != 0)
            throw new InvalidOperationException("Capstone review did not persist three clean task bookmarks.");

        Write(new
        {
            command = "capstone-hold",
            processId = Environment.ProcessId,
            workflowInstanceId = result.WorkflowState.Id,
            definitionVersionId = EdmsCapstoneDefinitionIdentity.Version1Id,
            status = result.WorkflowState.Status.ToString(),
            subStatus = result.WorkflowExecutionContext.SubStatus.ToString(),
            bookmarkIds = tasks.Select(task => task.BookmarkId).ToArray(),
            taskIds = tasks.Select(task => task.TaskId).ToArray(),
            disciplines = tasks.Select(task => task.Discipline).ToArray(),
            taskCount = tasks.Count,
            bookmarkCount = result.WorkflowState.Bookmarks.Count
        });

        // The parent verifies committed SQL rows and then kills this process tree while it is alive.
        await Console.In.ReadLineAsync();
        return 0;
    }

    public static async Task<int> CompleteAsync(string connectionString, string workflowInstanceId)
    {
        await SqlEdmsReviewTaskStore.EnsureSchemaAsync(connectionString);
        using var host = await CreateHostAsync(connectionString);
        await BuildV1GraphAsync(host.Services);
        var taskStore = host.Services.GetRequiredService<IEdmsReviewTaskStore>();
        var tasks = await taskStore.FindByWorkflowAsync(workflowInstanceId, CancellationToken.None);
        if (tasks.Count != 3)
            throw new InvalidOperationException($"Expected three SQL-backed EDMS review tasks, found {tasks.Count}.");

        // The capstone's application service adapter is process-local in this harness. Rebuild the
        // minimal domain row from the SQL task record; production stores would load the real revision aggregate.
        var first = tasks[0];
        await host.Services.GetRequiredService<IEdmsCapstoneService>().ReceiveRevisionAsync(
            new DocumentRevisionRecord(first.ProjectId, first.DocumentId, first.DocumentRevisionId,
                first.DocumentNumber, first.Revision, first.ReviewCycleId, "Incoming", null, null, false),
            CancellationToken.None);

        var taskService = host.Services.GetRequiredService<IEdmsReviewTaskApplicationService>();
        foreach (var task in tasks.OrderBy(task => task.Discipline, StringComparer.Ordinal))
        {
            var reviewer = task.CandidateUsers.Single();
            await taskService.ClaimAsync(task.TaskId, reviewer, CancellationToken.None);
            var decision = task.Discipline == "Mechanical" ? ReviewDecision.Commented : ReviewDecision.Approved;
            await taskService.CompleteAsync(task.TaskId, reviewer, decision,
                decision == ReviewDecision.Commented ? "Check the maintenance access clearance." : null,
                CancellationToken.None);
        }

        var state = await (await host.Services.GetRequiredService<IWorkflowRuntime>()
            .CreateClientAsync(workflowInstanceId)).ExportStateAsync();
        var instance = await host.Services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new Elsa.Workflows.Management.Filters.WorkflowInstanceFilter { Id = workflowInstanceId })
            ?? throw new InvalidOperationException("The SQL-backed workflow instance disappeared after task completion.");
        var completedTasks = await taskStore.FindByWorkflowAsync(workflowInstanceId, CancellationToken.None);
        Write(new
        {
            command = "capstone-complete",
            processId = Environment.ProcessId,
            workflowInstanceId,
            status = instance.Status.ToString(),
            subStatus = instance.SubStatus.ToString(),
            taskCount = completedTasks.Count,
            completedTaskCount = completedTasks.Count(task => task.Status == ReviewTaskStatus.Completed),
            bookmarksLeft = instance.WorkflowState.Bookmarks.Count,
            finalStatus = Convert.ToString(state.Output.GetValueOrDefault("FinalStatus")),
            commentCount = state.Output.GetValueOrDefault("CommentCount"),
            incidentCount = state.Incidents.Count,
            hasFaultedActivity = state.ActivityExecutionContexts.Any(context => context.Status == ActivityStatus.Faulted)
        });

        await host.StopAsync();
        return 0;
    }

    private static async Task<IHost> CreateHostAsync(string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Resilience:Strategies:0:$type"] = nameof(DocumentPublicationRetryStrategy),
            ["Resilience:Strategies:0:Id"] = "document-publication",
            ["Resilience:Strategies:0:DisplayName"] = "Capstone publication retry",
            ["Resilience:Strategies:0:MaxRetryAttempts"] = "2",
            ["Resilience:Strategies:0:Delay"] = "00:00:00.005"
        }).Build();
        var taskStore = new SqlEdmsReviewTaskStore(connectionString);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var services = builder.Services;
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IEdmsReviewTaskStore>(taskStore);
        services.AddSingleton<IEdmsCapstoneService, InMemoryEdmsCapstoneService>();
        services.AddSingleton<IEdmsReviewTaskApplicationService, EdmsReviewTaskApplicationService>();
        services.AddSingleton<IReviewSlaActionService, InMemoryReviewSlaActionService>();
        services.AddSingleton<IDocumentPublicationService, ProcessHarnessPublicationService>();
        services.AddSingleton<ITransientExceptionStrategy, DocumentServiceTransientExceptionStrategy>();
        services.AddElsa(elsa =>
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
            elsa.AddActivity<RegisterCapstoneRevisionActivity>();
            elsa.AddActivity<DistributeCapstoneRevisionActivity>();
            elsa.AddActivity<ConsolidateCapstoneReviewsActivity>();
            elsa.AddActivity<MarkCapstoneRevisionRequiredActivity>();
            elsa.AddActivity<MarkCapstoneRevisionPublishedActivity>();
            elsa.AddActivity<EdmsCoordinatorCheckActivity>();
            elsa.AddActivity<CreateCapstoneTransmittalActivity>();
            elsa.AddActivity<CompleteCapstoneWorkflowActivity>();
            elsa.AddActivity<ApproveCapstoneRevisionActivity>();
            elsa.AddActivity<MoveDocumentActivity>();
            elsa.AddActivity<PublishDocumentActivity>();
            elsa.AddWorkflow<EdmsEngineeringReviewV1Workflow>();
        });
        services.AddSingleton<ITaskDispatcher, EdmsReviewTaskDispatcher>();
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

    private static async Task<WorkflowGraph> BuildV1GraphAsync(IServiceProvider services)
    {
        var workflow = await services.GetRequiredService<IWorkflowBuilderFactory>()
            .CreateBuilder().BuildWorkflowAsync<EdmsEngineeringReviewV1Workflow>();
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        return await services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
    }

    private static void Write(object value) => Console.WriteLine(MessagePrefix + JsonSerializer.Serialize(value));

    private sealed class ProcessHarnessPublicationService : IDocumentPublicationService
    {
        public Task<DocumentPublicationResult> PublishAsync(DocumentPublicationCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DocumentPublicationResult(
                command.OperationId, DocumentPublicationStatus.Applied, command.DocumentNumber, command.Revision, command.Destination));
        }
    }
}
