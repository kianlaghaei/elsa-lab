using System.Text.Json;
using Elsa.Extensions;
using Elsa.Common.Models;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.Runtime.Messages;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class WorkflowVersioningProcessHarness
{
    private const string ResultPrefix = "ELSA_PROCESS_RESULT=";

    public static Task<int> RunAsync(string connectionString, string[] args) => args[0] switch
    {
        "versioning-seed-v1" when args.Length == 1 => SeedV1Async(connectionString),
        "versioning-deploy-v2" when args.Length == 3 => DeployV2AndResumeV1Async(connectionString, args[1], args[2]),
        _ => throw new ArgumentException($"Invalid arguments for command '{args[0]}'.")
    };

    private static async Task<int> SeedV1Async(string connectionString)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        var workflow = await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(host.Services);
        var definition = await FindDefinitionAsync(host.Services, DocumentReviewDefinitionIdentity.Version1Id);
        var response = await StartAsync(host.Services, WorkflowDefinitionHandle.ByDefinitionVersionId(definition.Id));
        var instance = await FindInstanceAsync(host.Services, response.WorkflowInstanceId);
        var bookmark = instance.WorkflowState.Bookmarks.Single();

        Ensure(instance.Status == WorkflowStatus.Running && instance.SubStatus == WorkflowSubStatus.Suspended,
            "Process A did not suspend V1 as expected.");

        WriteResult(new
        {
            command = "versioning-seed-v1",
            processId = Environment.ProcessId,
            definitionId = workflow.Identity.DefinitionId,
            definitionVersionId = instance.DefinitionVersionId,
            version = instance.Version,
            workflowInstanceId = instance.Id,
            bookmarkId = bookmark.Id,
            bookmarkName = bookmark.Name,
            bookmarkHash = bookmark.Hash,
            activityInstanceId = bookmark.ActivityInstanceId,
            status = instance.Status.ToString(),
            subStatus = instance.SubStatus.ToString()
        });

        await StopHostAsync(host);
        return 0;
    }

    private static async Task<int> DeployV2AndResumeV1Async(string connectionString, string v1InstanceId, string v1BookmarkId)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        await BuildAndRegisterAsync<DocumentReviewVersion1Workflow>(host.Services);
        await BuildAndRegisterAsync<DocumentReviewVersion2Workflow>(host.Services);

        var v1Definition = await FindDefinitionAsync(host.Services, DocumentReviewDefinitionIdentity.Version1Id);
        var v2Definition = await FindDefinitionAsync(host.Services, DocumentReviewDefinitionIdentity.Version2Id);
        var v1BeforeResume = await FindInstanceAsync(host.Services, v1InstanceId);
        Ensure(v1BeforeResume.DefinitionVersionId == v1Definition.Id && v1BeforeResume.Version == 1,
            "Persisted instance did not remain associated with the V1 version ID.");

        var newV2Response = await StartAsync(
            host.Services,
            WorkflowDefinitionHandle.ByDefinitionId(DocumentReviewDefinitionIdentity.DefinitionId, VersionOptions.Published));
        var newV2Instance = await FindInstanceAsync(host.Services, newV2Response.WorkflowInstanceId);
        Ensure(newV2Instance.DefinitionVersionId == v2Definition.Id && newV2Instance.Version == 2,
            "A new published start did not select V2.");
        Ensure(newV2Instance.Status == WorkflowStatus.Running && newV2Instance.SubStatus == WorkflowSubStatus.Suspended,
            "The new V2 instance did not suspend at its review bookmark.");

        var v1BookmarkBefore = await host.Services.GetRequiredService<IBookmarkStore>()
            .FindAsync(new BookmarkFilter { BookmarkId = v1BookmarkId })
            ?? throw new InvalidOperationException("The original V1 bookmark was missing after V2 deployment.");
        var v1Response = await host.Services.GetRequiredService<IWorkflowResumer>()
            .ResumeAsync(v1BookmarkId, new Dictionary<string, object>(), CancellationToken.None)
            ?? throw new InvalidOperationException("The exact V1 bookmark was not resumed.");
        var completedV1 = await FindInstanceAsync(host.Services, v1InstanceId);
        var remainingV2Bookmark = newV2Instance.WorkflowState.Bookmarks.Single();
        var storedV2Bookmark = await host.Services.GetRequiredService<IBookmarkStore>()
            .FindAsync(new BookmarkFilter { BookmarkId = remainingV2Bookmark.Id });

        Ensure(v1Response.WorkflowInstanceId == v1InstanceId, "Resume selected a different workflow instance.");
        Ensure(completedV1.Status == WorkflowStatus.Finished && completedV1.SubStatus == WorkflowSubStatus.Finished,
            "The V1 instance did not finish successfully.");
        Ensure(Equals(completedV1.WorkflowState.Output["DefinitionMarker"], "V1"),
            "The persisted V1 instance did not execute V1 finalization.");
        Ensure(Equals(completedV1.WorkflowState.Output["CoordinatorExecuted"], false),
            "The persisted V1 instance unexpectedly executed the V2 coordinator step.");
        Ensure(storedV2Bookmark != null, "The V2 bookmark was not retained after resuming V1.");

        WriteResult(new
        {
            command = "versioning-deploy-v2",
            processId = Environment.ProcessId,
            v1 = new
            {
                workflowInstanceId = completedV1.Id,
                definitionId = completedV1.DefinitionId,
                definitionVersionId = completedV1.DefinitionVersionId,
                version = completedV1.Version,
                status = completedV1.Status.ToString(),
                subStatus = completedV1.SubStatus.ToString(),
                marker = completedV1.WorkflowState.Output["DefinitionMarker"],
                coordinatorExecuted = completedV1.WorkflowState.Output["CoordinatorExecuted"],
                originalBookmarkHash = v1BookmarkBefore.Hash,
                originalBookmarkConsumed = await host.Services.GetRequiredService<IBookmarkStore>()
                    .FindAsync(new BookmarkFilter { BookmarkId = v1BookmarkId }) == null
            },
            v2 = new
            {
                workflowInstanceId = newV2Instance.Id,
                definitionId = newV2Instance.DefinitionId,
                definitionVersionId = newV2Instance.DefinitionVersionId,
                version = newV2Instance.Version,
                bookmarkId = remainingV2Bookmark.Id,
                bookmarkExists = storedV2Bookmark != null,
                status = newV2Instance.Status.ToString(),
                subStatus = newV2Instance.SubStatus.ToString()
            }
        });

        await StopHostAsync(host);
        return 0;
    }

    private static async Task<IHost> CreateAndStartHostAsync(string connectionString)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = Environment.CurrentDirectory
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
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
            elsa.AddActivity<WaitForDocumentReviewActivity>();
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

    private static async Task StopHostAsync(IHost host)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await host.StopAsync(timeout.Token);
    }

    private static async Task<Workflow> BuildAndRegisterAsync<TWorkflow>(IServiceProvider services)
        where TWorkflow : WorkflowBase, new()
    {
        var workflow = await services.GetRequiredService<IWorkflowBuilderFactory>()
            .CreateBuilder()
            .BuildWorkflowAsync<TWorkflow>();
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        return workflow;
    }

    private static async Task<WorkflowDefinition> FindDefinitionAsync(IServiceProvider services, string definitionVersionId) =>
        await services.GetRequiredService<IWorkflowDefinitionStore>().FindAsync(new WorkflowDefinitionFilter
        {
            Id = definitionVersionId
        }) ?? throw new InvalidOperationException($"Workflow definition version '{definitionVersionId}' was not found.");

    private static async Task<RunWorkflowInstanceResponse> StartAsync(IServiceProvider services, WorkflowDefinitionHandle handle)
    {
        var client = await services.GetRequiredService<IWorkflowRuntime>().CreateClientAsync();
        return await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = handle,
            IncludeWorkflowOutput = true
        });
    }

    private static async Task<WorkflowInstance> FindInstanceAsync(IServiceProvider services, string instanceId) =>
        await services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = instanceId })
        ?? throw new InvalidOperationException($"Workflow instance '{instanceId}' was not found.");

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void WriteResult(object result) =>
        Console.WriteLine(ResultPrefix + JsonSerializer.Serialize(result));
}
