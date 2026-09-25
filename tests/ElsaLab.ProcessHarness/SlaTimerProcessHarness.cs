using System.Text.Json;
using Elsa.Common.Models;
using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Entities;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class SlaTimerProcessHarness
{
    private const string JsonMessagePrefix = "ELSA_PROCESS_RESULT=";

    public static async Task<int> RunAsync(string connectionString, string[] args) => args[0] switch
    {
        "sla-start" when args.Length == 4 => await StartAsync(connectionString, args[1], args[2], args[3], hold: false),
        "sla-hold" when args.Length == 4 => await StartAsync(connectionString, args[1], args[2], args[3], hold: true),
        "sla-inspect" when args.Length == 2 => await InspectAsync(connectionString, args[1]),
        "sla-recover" when args.Length == 3 => await RecoverAsync(connectionString, args[1], int.Parse(args[2])),
        _ => throw new ArgumentException($"Invalid arguments for SLA timer command '{args[0]}'.")
    };

    private static async Task<int> StartAsync(string connectionString, string reviewKey, string initialDelayMilliseconds, string reminderDelayMilliseconds, bool hold)
    {
        var initialDelay = TimeSpan.FromMilliseconds(ParsePositive(initialDelayMilliseconds));
        var reminderDelay = TimeSpan.FromMilliseconds(ParsePositive(reminderDelayMilliseconds));
        var host = await CreateAndStartHostAsync(connectionString);
        try
        {
            var graph = await BuildAndRegisterWorkflowAsync(host.Services);
            await host.Services.GetRequiredService<IWorkflowDefinitionStorePopulator>().PopulateStoreAsync();
            var definition = await host.Services.GetRequiredService<IWorkflowDefinitionStore>()
                .FindAsync(new WorkflowDefinitionFilter { DefinitionId = graph.Workflow.Identity.DefinitionId })
                ?? throw new InvalidOperationException($"Code-first definition '{graph.Workflow.Identity.DefinitionId}' was not populated into Elsa's definition store.");
            var client = await host.Services.GetRequiredService<IWorkflowRuntime>().CreateClientAsync();
            var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
            {
                WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionVersionId(definition.Id),
                Input = CreateOptions(reviewKey, initialDelay, reminderDelay).Input,
                IncludeWorkflowOutput = true
            });
            var workflowState = await client.ExportStateAsync();
            EnsureSuspended(response, workflowState);

            var instanceId = workflowState.Id;
            var snapshot = await LoadSnapshotAsync(host.Services, instanceId);
            if (snapshot.Bookmarks.Length != 1)
                throw new InvalidOperationException($"Expected one initial Delay bookmark, found {snapshot.Bookmarks.Length}.");

            WriteMessage(new
            {
                command = hold ? "sla-hold" : "sla-start",
                processId = Environment.ProcessId,
                workflowInstanceId = snapshot.Instance.Id,
                bookmarkId = snapshot.Bookmarks[0].Id,
                bookmarkName = snapshot.Bookmarks[0].Name,
                bookmarkPayload = snapshot.Bookmarks[0].Payload,
                status = snapshot.Instance.Status.ToString(),
                subStatus = snapshot.Instance.SubStatus.ToString(),
                reminderCount = 0,
                escalationCount = 0,
                incidentCount = snapshot.Instance.WorkflowState.Incidents.Count,
                bookmarkCount = snapshot.Bookmarks.Length
            });

            if (hold)
            {
                // The parent verifies the SQL rows before terminating this process.
                await Console.In.ReadLineAsync();
                return 0;
            }

            await StopHostAsync(host);
            return 0;
        }
        finally
        {
            host.Dispose();
        }
    }

    private static async Task<int> InspectAsync(string connectionString, string workflowInstanceId)
    {
        var host = await CreateAndStartHostAsync(connectionString);
        try
        {
            var snapshot = await LoadSnapshotAsync(host.Services, workflowInstanceId);
            WriteMessage(new
            {
                command = "sla-inspect",
                processId = Environment.ProcessId,
                workflowInstanceId = snapshot.Instance.Id,
                status = snapshot.Instance.Status.ToString(),
                subStatus = snapshot.Instance.SubStatus.ToString(),
                bookmarkCount = snapshot.Bookmarks.Length,
                bookmarks = snapshot.Bookmarks.Select(bookmark => new
                {
                    bookmark.Id,
                    bookmark.Name,
                    bookmark.Hash,
                    bookmark.ActivityInstanceId,
                    bookmark.Payload
                }),
                outputs = snapshot.Instance.WorkflowState.Output,
                actionCount = host.Services.GetRequiredService<IReviewSlaActionService>().Actions.Count,
                incidentCount = snapshot.Instance.WorkflowState.Incidents.Count
            });

            await StopHostAsync(host);
            return 0;
        }
        finally
        {
            host.Dispose();
        }
    }

    private static async Task<int> RecoverAsync(string connectionString, string workflowInstanceId, int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 300 seconds.");

        var hostStartedAt = DateTimeOffset.UtcNow;
        var host = await CreateAndStartHostAsync(connectionString);
        try
        {
            var store = host.Services.GetRequiredService<IWorkflowInstanceStore>();
            var bookmarkStore = host.Services.GetRequiredService<IBookmarkStore>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
            WorkflowInstance? instance = null;

            while (DateTimeOffset.UtcNow < deadline)
            {
                instance = await store.FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId });
                if (instance is null)
                    throw new InvalidOperationException($"Workflow instance '{workflowInstanceId}' was not found in the persisted management store.");

                if (instance.Status == WorkflowStatus.Finished && instance.SubStatus == WorkflowSubStatus.Finished)
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            if (instance is null || instance.Status != WorkflowStatus.Finished || instance.SubStatus != WorkflowSubStatus.Finished)
                throw new TimeoutException($"Workflow '{workflowInstanceId}' did not finish before the {timeoutSeconds}-second recovery timeout.");

            var remaining = await bookmarkStore.FindManyAsync(
                new BookmarkFilter { WorkflowInstanceId = workflowInstanceId },
                PageArgs.FromRange(0, 100));
            var response = new
            {
                command = "sla-recover",
                processId = Environment.ProcessId,
                hostStartedAt,
                completedAt = DateTimeOffset.UtcNow,
                workflowInstanceId = instance.Id,
                status = instance.Status.ToString(),
                subStatus = instance.SubStatus.ToString(),
                bookmarkCount = remaining.Items.Count,
                actions = host.Services.GetRequiredService<IReviewSlaActionService>().Actions,
                outputs = instance.WorkflowState.Output,
                incidentCount = instance.WorkflowState.Incidents.Count
            };

            WriteMessage(response);
            await StopHostAsync(host);
            return 0;
        }
        finally
        {
            host.Dispose();
        }
    }

    private static async Task<IHost> CreateAndStartHostAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = Environment.CurrentDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton<IReviewSlaActionService, InMemoryReviewSlaActionService>();
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
            elsa.UseScheduling(_ => { });
            elsa.AddActivity<SendReviewReminderActivity>();
            elsa.AddActivity<EscalateReviewActivity>();
            elsa.AddWorkflow<ReviewSlaWorkflow>();
            elsa.AddWorkflow<ReviewSlaRaceWorkflow>();
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

    private static async Task<WorkflowGraph> BuildAndRegisterWorkflowAsync(IServiceProvider services)
    {
        var workflow = await services.GetRequiredService<IWorkflowBuilderFactory>()
            .CreateBuilder()
            .BuildWorkflowAsync<ReviewSlaWorkflow>();
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        return await services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
    }

    private static RunWorkflowOptions CreateOptions(string reviewKey, TimeSpan initialDelay, TimeSpan reminderDelay) => new()
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = "DPC-10-ME-0001",
            ["ReviewKey"] = reviewKey,
            ["InitialDelay"] = initialDelay,
            ["ReminderDelay"] = reminderDelay
        }
    };

    private static async Task<(WorkflowInstance Instance, StoredBookmark[] Bookmarks)> LoadSnapshotAsync(IServiceProvider services, string workflowInstanceId)
    {
        var instance = await services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
            ?? throw new InvalidOperationException($"Workflow instance '{workflowInstanceId}' was not committed to SQL.");
        var bookmarks = await services.GetRequiredService<IBookmarkStore>()
            .FindManyAsync(new BookmarkFilter { WorkflowInstanceId = workflowInstanceId }, PageArgs.FromRange(0, 100));
        return (instance, bookmarks.Items.ToArray());
    }

    private static void EnsureSuspended(RunWorkflowInstanceResponse response, WorkflowState state)
    {
        if (response.Status != WorkflowStatus.Running ||
            response.SubStatus != WorkflowSubStatus.Suspended ||
            state.Bookmarks.Count != 1 ||
            state.Incidents.Count != 0 ||
            state.ActivityExecutionContexts.Any(context => context.Status == ActivityStatus.Faulted))
            throw new InvalidOperationException("The ReviewSlaWorkflow did not remain cleanly suspended on its initial Delay.");
    }

    private static int ParsePositive(string value)
    {
        var milliseconds = int.Parse(value);
        return milliseconds > 0 ? milliseconds : throw new ArgumentOutOfRangeException(nameof(value), "Delay must be positive.");
    }

    private static async Task StopHostAsync(IHost host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StopAsync(timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
    }

    private static void WriteMessage(object value) =>
        Console.WriteLine(JsonMessagePrefix + JsonSerializer.Serialize(value));
}
