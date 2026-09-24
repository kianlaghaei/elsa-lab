using System.Dynamic;
using System.Globalization;
using System.Text.Json;
using Elsa.Common;
using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElsaLab.Tests;

public class ElsaSqlServerPersistenceTests
{
    private const string SqlServerConnectionVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task SuspendedInstanceAndBookmark_PersistAcrossFreshProviders_AndResumeFromSql()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());

        string workflowInstanceId;
        string bookmarkId;
        string bookmarkName;
        string activityInstanceId;
        string bookmarkHash;
        string workflowDefinitionId;
        string workflowDefinitionVersionId;

        await using (var hostA = await ElsaSqlTestHost.CreateAsync(database.ConnectionString))
        {
            using var scopeA = hostA.Services.CreateScope();
            var servicesA = scopeA.ServiceProvider;
            var (workflow, graph) = await BuildAndRegisterWorkflowAsync(servicesA);
            workflowDefinitionId = workflow.Identity.DefinitionId!;
            workflowDefinitionVersionId = workflow.Identity.Id!;

            var started = await servicesA.GetRequiredService<IWorkflowRunner>().RunAsync(
                graph,
                CreateStartOptions("DPC-10-ME-0001", revision: 2, "discipline-review"));
            AssertSuspended(started);

            workflowInstanceId = started.WorkflowState.Id;

            var instanceStore = servicesA.GetRequiredService<IWorkflowInstanceStore>();
            var storedInstance = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
                ?? throw new Xunit.Sdk.XunitException("Host A did not persist the suspended workflow instance.");
            Assert.Equal(WorkflowStatus.Running, storedInstance.Status);
            Assert.Equal(WorkflowSubStatus.Suspended, storedInstance.SubStatus);
            Assert.Equal(workflowDefinitionVersionId, storedInstance.DefinitionVersionId);
            var bookmark = Assert.Single(storedInstance.WorkflowState.Bookmarks);
            bookmarkId = bookmark.Id;
            bookmarkName = bookmark.Name;
            bookmarkHash = bookmark.Hash;
            activityInstanceId = bookmark.ActivityInstanceId
                ?? throw new Xunit.Sdk.XunitException("The persisted bookmark has no ActivityInstanceId.");
            Assert.Equal("DocumentReview", bookmarkName);
            Assert.Equal("DPC-10-ME-0001", ReadBookmarkPayload(bookmark.Payload).DocumentNumber);
            Assert.DoesNotContain(storedInstance.WorkflowState.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
            var waitContextState = Assert.Single(storedInstance.WorkflowState.ActivityExecutionContexts,
                context => context.ScheduledActivityNodeId.Contains("WaitForDocumentReviewActivity", StringComparison.Ordinal))!;
            var waitActivityState = waitContextState.ActivityState
                ?? throw new Xunit.Sdk.XunitException("The persisted wait Activity state was missing.");
            Assert.Equal("DPC-10-ME-0001", waitActivityState["DocumentNumber"]);
            Assert.Equal(2, ReadIntOutput(waitActivityState["Revision"]));
            Assert.Equal("discipline-review", waitActivityState["ReviewKey"]);
            var workflowStateBookmark = Assert.Single(storedInstance.WorkflowState.Bookmarks);
            Assert.True(workflowStateBookmark.AutoBurn);
            Assert.True(workflowStateBookmark.AutoComplete);

            var bookmarkStore = servicesA.GetRequiredService<IBookmarkStore>();
            var storedBookmark = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkId });
            Assert.NotNull(storedBookmark);
            Assert.Equal(workflowInstanceId, storedBookmark.WorkflowInstanceId);
            Assert.Equal(activityInstanceId, storedBookmark.ActivityInstanceId);

            var managementFactory = servicesA.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>();
            await using var managementDb = await managementFactory.CreateDbContextAsync();
            Assert.Equal(1, await managementDb.WorkflowInstances.AsNoTracking().CountAsync(item => item.Id == workflowInstanceId));
            Assert.Equal(1, await managementDb.WorkflowDefinitions.AsNoTracking().CountAsync(item => item.Id == workflowDefinitionVersionId));

            var runtimeFactory = servicesA.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>();
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync();
            Assert.Equal(1, await runtimeDb.Bookmarks.AsNoTracking().CountAsync(item => item.Id == bookmarkId));
        }

        // No Elsa provider, scope, graph, WorkflowState, or bookmark object from Host A is used below.
        await using (var hostB = await ElsaSqlTestHost.CreateAsync(database.ConnectionString))
        {
            using var scopeB = hostB.Services.CreateScope();
            var servicesB = scopeB.ServiceProvider;

            var instanceStore = servicesB.GetRequiredService<IWorkflowInstanceStore>();
            var persistedInstance = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
                ?? throw new Xunit.Sdk.XunitException("Host B could not load the persisted workflow instance.");
            Assert.Equal(workflowInstanceId, persistedInstance.Id);
            Assert.Equal(WorkflowStatus.Running, persistedInstance.Status);
            Assert.Equal(WorkflowSubStatus.Suspended, persistedInstance.SubStatus);
            Assert.Empty(persistedInstance.WorkflowState.Input);
            Assert.Empty(persistedInstance.WorkflowState.Output);
            var restoredWaitContext = Assert.Single(persistedInstance.WorkflowState.ActivityExecutionContexts,
                context => context.ScheduledActivityNodeId.Contains("WaitForDocumentReviewActivity", StringComparison.Ordinal))!;
            var restoredActivityState = restoredWaitContext.ActivityState
                ?? throw new Xunit.Sdk.XunitException("The rehydrated wait Activity state was missing.");
            Assert.Equal("DPC-10-ME-0001", restoredActivityState["DocumentNumber"]);
            Assert.Equal(2, ReadIntOutput(restoredActivityState["Revision"]));
            Assert.Equal("discipline-review", restoredActivityState["ReviewKey"]);

            var persistedDefinitionStore = servicesB.GetRequiredService<IWorkflowDefinitionStore>();
            var definitions = (await persistedDefinitionStore.FindManyAsync(
                new WorkflowDefinitionFilter { DefinitionId = workflowDefinitionId })).ToList();
            Assert.Contains(definitions, definition => definition.Id == workflowDefinitionVersionId);

            var bookmarkStore = servicesB.GetRequiredService<IBookmarkStore>();
            var persistedBookmark = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkId });
            Assert.NotNull(persistedBookmark);
            Assert.Equal(bookmarkId, persistedBookmark.Id);
            Assert.Equal(bookmarkName, persistedBookmark.Name);
            Assert.Equal(bookmarkHash, persistedBookmark.Hash);
            Assert.Equal(workflowInstanceId, persistedBookmark.WorkflowInstanceId);
            Assert.Equal(activityInstanceId, persistedBookmark.ActivityInstanceId);
            Assert.Equal("discipline-review", persistedBookmark.Metadata!["ReviewKey"]);

            var payload = ReadBookmarkPayload(persistedBookmark.Payload);
            Assert.Equal(typeof(ExpandoObject).FullName, payload.ClrType);
            Assert.Equal("DPC-10-ME-0001", payload.DocumentNumber);
            Assert.Equal(2, payload.Revision);
            Assert.Equal("discipline-review", payload.ReviewKey);

            var managementFactory = servicesB.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>();
            await using var managementDb = await managementFactory.CreateDbContextAsync();
            Assert.Equal(1, await managementDb.WorkflowInstances.AsNoTracking().CountAsync(item => item.Id == workflowInstanceId));

            var runtimeFactory = servicesB.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>();
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync();
            Assert.Equal(1, await runtimeDb.Bookmarks.AsNoTracking().CountAsync(item => item.Id == bookmarkId));

            // A persisted definition row alone does not populate the new host's code-first Activity materialization context.
            var resumer = servicesB.GetRequiredService<IWorkflowResumer>();
            var materializationFailure = await Record.ExceptionAsync(() => resumer.ResumeAsync(
                bookmarkId,
                new Dictionary<string, object>(),
                CancellationToken.None));
            Assert.IsType<NullReferenceException>(materializationFailure);
            var stillSuspended = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
                ?? throw new Xunit.Sdk.XunitException("Host B lost the workflow after the failed materialization attempt.");
            Assert.Equal(WorkflowSubStatus.Suspended, stillSuspended.SubStatus);
            Assert.NotNull(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkId }));

            // Rebuild and register the compiled code-first definition through Elsa's native registry on Host B.
            var (workflowB, _) = await BuildAndRegisterWorkflowAsync(servicesB);
            Assert.Equal(workflowDefinitionId, workflowB.Identity.DefinitionId);

            var response = await resumer.ResumeAsync(
                bookmarkId,
                new Dictionary<string, object>(),
                CancellationToken.None);

            Assert.NotNull(response);
            Assert.Equal(workflowInstanceId, response.WorkflowInstanceId);
            Assert.Equal(WorkflowStatus.Finished, response.Status);
            Assert.Equal(WorkflowSubStatus.Finished, response.SubStatus);
            Assert.Empty(response.Bookmarks);
            Assert.Empty(response.Incidents);
            Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkId }));

            var completedInstance = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
                ?? throw new Xunit.Sdk.XunitException("Host B did not persist the completed workflow instance.");
            Assert.Equal(WorkflowStatus.Finished, completedInstance.Status);
            Assert.Equal(WorkflowSubStatus.Finished, completedInstance.SubStatus);
            Assert.Empty(completedInstance.WorkflowState.Bookmarks);
            Assert.Equal("ReviewCompleted", completedInstance.WorkflowState.Output["FinalStatus"]);
            Assert.Equal(true, completedInstance.WorkflowState.Output["Finalized"]);
            Assert.Equal("DPC-10-ME-0001", completedInstance.WorkflowState.Output["FinalDocumentNumber"]);
            Assert.Equal(2, ReadIntOutput(completedInstance.WorkflowState.Output["FinalRevision"]));
            Assert.Equal("discipline-review", completedInstance.WorkflowState.Output["FinalReviewKey"]);
            Assert.Empty(completedInstance.WorkflowState.Incidents);

            var completedManagementFactory = servicesB.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>();
            await using var completedManagementDb = await completedManagementFactory.CreateDbContextAsync();
            var persistedCompletedRow = await completedManagementDb.WorkflowInstances.AsNoTracking()
                .SingleAsync(item => item.Id == workflowInstanceId);
            Assert.Equal(WorkflowStatus.Finished, persistedCompletedRow.Status);
            Assert.Equal(WorkflowSubStatus.Finished, persistedCompletedRow.SubStatus);

            var completedRuntimeFactory = servicesB.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>();
            await using var completedRuntimeDb = await completedRuntimeFactory.CreateDbContextAsync();
            Assert.Equal(0, await completedRuntimeDb.Bookmarks.AsNoTracking().CountAsync(item => item.Id == bookmarkId));
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task TwoPersistedSuspendedInstances_ResumeIndependentlyFromFreshProvider()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());

        string instanceAId;
        string instanceBId;
        string bookmarkAId;
        string bookmarkBId;

        await using (var hostA = await ElsaSqlTestHost.CreateAsync(database.ConnectionString))
        {
            using var scopeA = hostA.Services.CreateScope();
            var servicesA = scopeA.ServiceProvider;
            var (_, graph) = await BuildAndRegisterWorkflowAsync(servicesA);
            var startedA = await servicesA.GetRequiredService<IWorkflowRunner>().RunAsync(
                graph,
                CreateStartOptions("DPC-10-ME-0001", 2, "review-A"));
            var startedB = await servicesA.GetRequiredService<IWorkflowRunner>().RunAsync(
                graph,
                CreateStartOptions("DPC-10-ME-0002", 5, "review-B"));
            AssertSuspended(startedA);
            AssertSuspended(startedB);

            instanceAId = startedA.WorkflowState.Id;
            instanceBId = startedB.WorkflowState.Id;
            bookmarkAId = Assert.Single(startedA.WorkflowState.Bookmarks).Id;
            bookmarkBId = Assert.Single(startedB.WorkflowState.Bookmarks).Id;
            Assert.NotEqual(instanceAId, instanceBId);
            Assert.NotEqual(bookmarkAId, bookmarkBId);

            var instanceStore = servicesA.GetRequiredService<IWorkflowInstanceStore>();
            Assert.NotNull(await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceAId }));
            Assert.NotNull(await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceBId }));
        }

        await using (var hostB = await ElsaSqlTestHost.CreateAsync(database.ConnectionString))
        {
            using var scopeB = hostB.Services.CreateScope();
            var servicesB = scopeB.ServiceProvider;
            var instanceStore = servicesB.GetRequiredService<IWorkflowInstanceStore>();
            var bookmarkStore = servicesB.GetRequiredService<IBookmarkStore>();

            await BuildAndRegisterWorkflowAsync(servicesB);

            var instanceA = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceAId });
            var instanceB = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceBId });
            Assert.NotNull(instanceA);
            Assert.NotNull(instanceB);
            Assert.Equal(WorkflowSubStatus.Suspended, instanceA.SubStatus);
            Assert.Equal(WorkflowSubStatus.Suspended, instanceB.SubStatus);
            var bookmarkA = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkAId });
            var bookmarkB = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkBId });
            Assert.NotNull(bookmarkA);
            Assert.NotNull(bookmarkB);
            Assert.Equal(instanceAId, bookmarkA.WorkflowInstanceId);
            Assert.Equal(instanceBId, bookmarkB.WorkflowInstanceId);
            Assert.Equal("DPC-10-ME-0001", ReadBookmarkPayload(bookmarkA.Payload).DocumentNumber);
            Assert.Equal("DPC-10-ME-0002", ReadBookmarkPayload(bookmarkB.Payload).DocumentNumber);

            var resumer = servicesB.GetRequiredService<IWorkflowResumer>();
            var responseA = await resumer.ResumeAsync(bookmarkAId, new Dictionary<string, object>(), CancellationToken.None);
            Assert.NotNull(responseA);
            Assert.Equal(instanceAId, responseA.WorkflowInstanceId);
            Assert.True(responseA.Status == WorkflowStatus.Finished,
                string.Join(" | ", responseA.Incidents.Select(DescribeIncident)));
            Assert.True(responseA.SubStatus == WorkflowSubStatus.Finished,
                string.Join(" | ", responseA.Incidents.Select(DescribeIncident)));
            Assert.Empty(responseA.Incidents);
            Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkAId }));

            instanceB = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceBId });
            Assert.NotNull(instanceB);
            Assert.Equal(WorkflowStatus.Running, instanceB.Status);
            Assert.Equal(WorkflowSubStatus.Suspended, instanceB.SubStatus);
            Assert.Empty(instanceB.WorkflowState.Output);
            Assert.NotNull(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkBId }));

            var responseB = await resumer.ResumeAsync(bookmarkBId, new Dictionary<string, object>(), CancellationToken.None);
            Assert.NotNull(responseB);
            Assert.Equal(instanceBId, responseB.WorkflowInstanceId);
            Assert.Equal(WorkflowStatus.Finished, responseB.Status);
            Assert.Equal(WorkflowSubStatus.Finished, responseB.SubStatus);
            Assert.Empty(responseB.Incidents);
            Assert.Null(await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkBId }));

            instanceA = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceAId });
            instanceB = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = instanceBId });
            Assert.NotNull(instanceA);
            Assert.NotNull(instanceB);
            Assert.Empty(instanceA.WorkflowState.Incidents);
            Assert.Empty(instanceB.WorkflowState.Incidents);
            Assert.Equal("ReviewCompleted", instanceA.WorkflowState.Output["FinalStatus"]);
            Assert.Equal(true, instanceA.WorkflowState.Output["Finalized"]);
            Assert.Equal("DPC-10-ME-0001", instanceA.WorkflowState.Output["FinalDocumentNumber"]);
            Assert.Equal(2, ReadIntOutput(instanceA.WorkflowState.Output["FinalRevision"]));
            Assert.Equal("review-A", instanceA.WorkflowState.Output["FinalReviewKey"]);
            Assert.Equal("DPC-10-ME-0002", instanceB.WorkflowState.Output["FinalDocumentNumber"]);
            Assert.Equal(5, ReadIntOutput(instanceB.WorkflowState.Output["FinalRevision"]));
            Assert.Equal("review-B", instanceB.WorkflowState.Output["FinalReviewKey"]);
            Assert.Equal("ReviewCompleted", instanceB.WorkflowState.Output["FinalStatus"]);
            Assert.Equal(true, instanceB.WorkflowState.Output["Finalized"]);
        }
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable(SqlServerConnectionVariable)
        ?? throw new InvalidOperationException($"{SqlServerConnectionVariable} is required for SQL Server integration tests.");

    private static async Task<(Workflow Workflow, WorkflowGraph Graph)> BuildAndRegisterWorkflowAsync(IServiceProvider services)
    {
        var builder = services.GetRequiredService<IWorkflowBuilderFactory>().CreateBuilder();
        var workflow = await builder.BuildWorkflowAsync<DocumentReviewBlockingWorkflow>();
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        var graph = await services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
        return (workflow, graph);
    }

    private static RunWorkflowOptions CreateStartOptions(string documentNumber, int revision, string reviewKey) => new()
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = documentNumber,
            ["Revision"] = revision,
            ["ReviewKey"] = reviewKey,
            ["ReviewOutcome"] = "Pending",
            ["Reviewer"] = "Unassigned",
            ["PublishReviewPayloadOnResume"] = true
        }
    };

    private static void AssertSuspended(RunWorkflowResult result)
    {
        Assert.Equal(WorkflowStatus.Running, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, result.WorkflowExecutionContext.SubStatus);
        Assert.Single(result.WorkflowState.Bookmarks);
        Assert.DoesNotContain(result.Journal.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
        Assert.DoesNotContain(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "FinalizeDocument");
    }

    private static BookmarkPayloadSnapshot ReadBookmarkPayload(object? payload)
    {
        Assert.NotNull(payload);
        switch (payload)
        {
            case DocumentReviewBookmarkPayload typed:
                return new(payload.GetType().FullName!, typed.DocumentNumber, typed.Revision, typed.ReviewKey);
            case IDictionary<string, object> dictionary:
                return new(
                    payload.GetType().FullName!,
                    Assert.IsType<string>(dictionary["documentNumber"]),
                    Convert.ToInt32(dictionary["revision"], CultureInfo.InvariantCulture),
                    Assert.IsType<string>(dictionary["reviewKey"]));
            case JsonElement json:
                return new(
                    payload.GetType().FullName!,
                    json.GetProperty("documentNumber").GetString()!,
                    json.GetProperty("revision").GetInt32(),
                    json.GetProperty("reviewKey").GetString()!);
            default:
                throw new Xunit.Sdk.XunitException($"Unexpected bookmark payload type after persistence: {payload.GetType().FullName}");
        }
    }

    private sealed record BookmarkPayloadSnapshot(string ClrType, string DocumentNumber, int Revision, string ReviewKey);

    private static int ReadIntOutput(object? value) => value switch
    {
        int integer => integer,
        JsonElement json => json.GetInt32(),
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
    };

    private static string DescribeIncident(object incident) =>
        string.Join(", ", incident.GetType().GetProperties().Select(property =>
        {
            var value = property.GetValue(incident);
            return $"{property.Name}={(value is Exception exception ? exception.Message : value)}";
        }));
}

internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ELSALAB_SQLSERVER_CONNECTION_STRING")))
            Skip = "Set ELSALAB_SQLSERVER_CONNECTION_STRING to run SQL Server integration tests.";
    }
}

internal sealed class ElsaSqlTestHost : IAsyncDisposable
{
    private ElsaSqlTestHost(ServiceProvider services) => Services = services;

    public ServiceProvider Services { get; }

    public static async Task<ElsaSqlTestHost> CreateAsync(string connectionString)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        serviceCollection.AddElsa(elsa =>
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
            elsa.AddWorkflow<DocumentReviewBlockingWorkflow>();
        });

        var services = serviceCollection.BuildServiceProvider();
        var host = new ElsaSqlTestHost(services);
        try
        {
            await host.RunElsaMigrationsAsync();
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private async Task RunElsaMigrationsAsync()
    {
        using var scope = Services.CreateScope();
        var startupTasks = scope.ServiceProvider.GetServices<IStartupTask>().ToList();
        var managementMigration = startupTasks.OfType<RunMigrationsStartupTask<ManagementElsaDbContext>>().FirstOrDefault();
        var runtimeMigration = startupTasks.OfType<RunMigrationsStartupTask<RuntimeElsaDbContext>>().FirstOrDefault();

        if (managementMigration is null || runtimeMigration is null)
            throw new InvalidOperationException("Elsa did not register both management and runtime EF Core migration startup tasks.");

        await managementMigration.ExecuteAsync(CancellationToken.None);
        await runtimeMigration.ExecuteAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

internal sealed class SqlServerTestDatabase : IAsyncDisposable
{
    private readonly string _masterConnectionString;

    private SqlServerTestDatabase(string databaseName, string masterConnectionString, string connectionString)
    {
        DatabaseName = databaseName;
        _masterConnectionString = masterConnectionString;
        ConnectionString = connectionString;
    }

    public string DatabaseName { get; }
    public string ConnectionString { get; }

    public static async Task<SqlServerTestDatabase> CreateAsync(string serverConnectionString)
    {
        var databaseName = $"ElsaLab_{Guid.NewGuid():N}";
        var masterBuilder = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = "master",
            ApplicationName = "ElsaLab ELSA-09 test database provisioner"
        };
        var databaseBuilder = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = databaseName,
            ApplicationName = "ElsaLab ELSA-09 SQL integration test"
        };

        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();

        return new SqlServerTestDatabase(databaseName, masterBuilder.ConnectionString, databaseBuilder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(_masterConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END";
        await command.ExecuteNonQueryAsync();
    }
}
