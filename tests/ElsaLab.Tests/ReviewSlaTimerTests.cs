using System.Globalization;
using System.Text.Json;
using Elsa.Common.Models;
using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Scheduling.Bookmarks;
using Elsa.Scheduling.Services;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ElsaLab.Tests;

public sealed class ReviewSlaTimerTests
{
    private const string SqlServerConnectionVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";

    [SlaTimerFact]
    [Trait("Category", "SlaTimer")]
    [Trait("Category", "SqlServer")]
    public async Task Delay_PersistsBookmarkAndBlocksDownstreamBeforeDueTime()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await ElsaSlaSqlHost.StartAsync(database.ConnectionString);
        var graph = await BuildAndRegisterWorkflowAsync<ReviewSlaWorkflow>(host.Services);

        var run = await RunWorkflowAsync(host.Services, graph,
            CreateOptions("sla-before-due", TimeSpan.FromHours(1), TimeSpan.FromSeconds(2)));

        Assert.Equal(WorkflowStatus.Running, run.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, run.SubStatus);
        Assert.Empty(run.WorkflowState.Incidents);
        Assert.DoesNotContain(run.WorkflowState.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Empty(host.Services.GetRequiredService<IReviewSlaActionService>().Actions);

        var workflowInstanceId = run.WorkflowState.Id;
        var persisted = await WaitForInstanceAsync(host.Services, workflowInstanceId, instance => instance.Status == WorkflowStatus.Running);
        Assert.Equal(WorkflowSubStatus.Suspended, persisted.SubStatus);

        var bookmarks = await FindBookmarksAsync(host.Services, workflowInstanceId);
        var bookmark = Assert.Single(bookmarks);
        Assert.Equal(workflowInstanceId, bookmark.WorkflowInstanceId);
        Assert.True(ReadResumeAt(bookmark.Payload) > DateTimeOffset.UtcNow);
        Assert.False(persisted.WorkflowState.Output.ContainsKey("ReminderCount"));

        var runtimeFactory = host.Services.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>();
        await using var runtimeDb = await runtimeFactory.CreateDbContextAsync();
        Assert.Equal(1, await runtimeDb.Bookmarks.AsNoTracking().CountAsync(item => item.Id == bookmark.Id));

    }

    [SlaTimerFact]
    [Trait("Category", "SlaTimer")]
    [Trait("Category", "SqlServer")]
    public async Task DelayFires_ReminderThenEscalationRunOnce_InOrder_AndFinish()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await ElsaSlaSqlHost.StartAsync(database.ConnectionString);
        var graph = await BuildAndRegisterWorkflowAsync<ReviewSlaWorkflow>(host.Services);
        var run = await RunWorkflowAsync(host.Services, graph,
            CreateOptions("sla-reminder-escalation", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));

        Assert.Equal(WorkflowSubStatus.Suspended, run.SubStatus);
        var initialDelayBookmark = Assert.Single(await FindBookmarksAsync(host.Services, run.WorkflowState.Id));
        var initialDelayContext = Assert.Single(
            run.WorkflowState.ActivityExecutionContexts,
            context => context.Id == initialDelayBookmark.ActivityInstanceId);
        Assert.Equal(ActivityStatus.Running, initialDelayContext.Status);
        var completed = await WaitForInstanceAsync(host.Services, run.WorkflowState.Id, instance =>
            instance.Status == WorkflowStatus.Finished && instance.SubStatus == WorkflowSubStatus.Finished);

        Assert.Equal("Finished", completed.Status.ToString());
        Assert.Equal("Finished", completed.SubStatus.ToString());
        Assert.Empty(completed.WorkflowState.Incidents);
        Assert.Equal(1, ReadIntOutput(completed.WorkflowState.Output["ReminderCount"]));
        Assert.Equal(1, ReadIntOutput(completed.WorkflowState.Output["EscalationCount"]));
        Assert.True(ReadBooleanOutput(completed.WorkflowState.Output["Escalated"]));
        Assert.Equal("Escalated", Assert.IsType<string>(completed.WorkflowState.Output["FinalSlaStatus"]));
        Assert.Equal("DPC-10-ME-0001", Assert.IsType<string>(completed.WorkflowState.Output["FinalDocumentNumber"]));
        Assert.Equal("sla-reminder-escalation", Assert.IsType<string>(completed.WorkflowState.Output["FinalReviewKey"]));
        Assert.DoesNotContain(completed.WorkflowState.ActivityExecutionContexts, context => context.Status == ActivityStatus.Faulted);
        Assert.Empty(await FindBookmarksAsync(host.Services, completed.Id));

        var logPage = await host.Services.GetRequiredService<IWorkflowExecutionLogStore>().FindManyAsync(
            new WorkflowExecutionLogRecordFilter { WorkflowInstanceId = completed.Id },
            PageArgs.FromRange(0, 500));
        var initialDelayLogs = logPage.Items.Where(item => item.ActivityName == "InitialSlaDelay")
            .OrderBy(item => item.Sequence).ToArray();
        Assert.Equal(new[] { "Started", "Suspended", "Resumed", "Completed" }, initialDelayLogs.Select(item => item.EventName));
        Assert.All(initialDelayLogs, item => Assert.Equal(initialDelayBookmark.ActivityInstanceId, item.ActivityInstanceId));
        var reminderCompleted = Assert.Single(logPage.Items, item => item.ActivityName == "SendReviewReminder" && item.EventName == "Completed");
        var escalationStarted = Assert.Single(logPage.Items, item => item.ActivityName == "EscalateReview" && item.EventName == "Started");
        Assert.True(reminderCompleted.Sequence < escalationStarted.Sequence);
        Assert.Equal(reminderCompleted.ParentActivityInstanceId, escalationStarted.ParentActivityInstanceId);

        var actions = host.Services.GetRequiredService<IReviewSlaActionService>().Actions;
        Assert.Collection(
            actions,
            action => Assert.Equal(new ReviewSlaActionCommand("Reminder:sla-reminder-escalation:1", "sla-reminder-escalation", ReviewSlaActionKind.Reminder, 1), action),
            action => Assert.Equal(new ReviewSlaActionCommand("Escalation:sla-reminder-escalation", "sla-reminder-escalation", ReviewSlaActionKind.Escalation, 0), action));
        Assert.Single(actions, action => action.Kind == ReviewSlaActionKind.Reminder);
        Assert.Single(actions, action => action.Kind == ReviewSlaActionKind.Escalation);

        var managementFactory = host.Services.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>();
        await using var managementDb = await managementFactory.CreateDbContextAsync();
        var stored = await managementDb.WorkflowInstances.AsNoTracking().SingleAsync(item => item.Id == completed.Id);
        Assert.Equal(WorkflowStatus.Finished, stored.Status);
        Assert.Equal(WorkflowSubStatus.Finished, stored.SubStatus);
    }

    [SlaTimerFact]
    [Trait("Category", "SlaTimer")]
    [Trait("Category", "SqlServer")]
    public async Task ReviewCompletedBeforeDeadline_CancelsTimerBookmark_AndPreventsReminderOrEscalation()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await ElsaSlaSqlHost.StartAsync(database.ConnectionString);
        var graph = await BuildAndRegisterWorkflowAsync<ReviewSlaRaceWorkflow>(host.Services);
        var run = await RunWorkflowAsync(host.Services, graph,
            CreateOptions("sla-complete-first", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)));

        Assert.Equal(WorkflowStatus.Running, run.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, run.SubStatus);
        Assert.Equal(2, run.WorkflowState.Bookmarks.Count);

        var persistedBookmarks = await FindBookmarksAsync(host.Services, run.WorkflowState.Id);
        var timerBookmark = Assert.Single(persistedBookmarks, bookmark => bookmark.Payload is DelayPayload);
        var eventBookmark = Assert.Single(persistedBookmarks, bookmark => bookmark.Id != timerBookmark.Id);
        Assert.NotEqual(eventBookmark.Id, timerBookmark.Id);

        var response = await host.Services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            eventBookmark.Id,
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.NotNull(response);
        var resumeResponse = response ?? throw new Xunit.Sdk.XunitException("Resume did not return a workflow response.");
        Assert.Equal(run.WorkflowState.Id, resumeResponse.WorkflowInstanceId);
        Assert.Equal(WorkflowStatus.Finished, resumeResponse.Status);
        Assert.Equal(WorkflowSubStatus.Finished, resumeResponse.SubStatus);
        Assert.Empty(resumeResponse.Incidents);
        var completed = await WaitForInstanceAsync(host.Services, run.WorkflowState.Id, instance =>
            instance.Status == WorkflowStatus.Finished && instance.SubStatus == WorkflowSubStatus.Finished);
        Assert.Equal(0, ReadIntOutput(completed.WorkflowState.Output["ReminderCount"]));
        Assert.False(ReadBooleanOutput(completed.WorkflowState.Output["Escalated"]));
        Assert.Equal("OnTime", Assert.IsType<string>(completed.WorkflowState.Output["FinalSlaStatus"]));
        Assert.Empty(await FindBookmarksAsync(host.Services, run.WorkflowState.Id));
        Assert.Empty(host.Services.GetRequiredService<IReviewSlaActionService>().Actions);

        // Keep the runtime alive beyond the original due time to detect a stale local schedule.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        completed = await WaitForInstanceAsync(host.Services, run.WorkflowState.Id, instance => instance.Status == WorkflowStatus.Finished);
        Assert.Equal(0, ReadIntOutput(completed.WorkflowState.Output["ReminderCount"]));
        Assert.Empty(host.Services.GetRequiredService<IReviewSlaActionService>().Actions);
        Assert.Empty(await FindBookmarksAsync(host.Services, run.WorkflowState.Id));
    }

    [SlaTimerFact]
    [Trait("Category", "SlaTimer")]
    [Trait("Category", "SqlServer")]
    public async Task TwoSlaWorkflows_KeepIndependentDeadlinesAndActions()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        await using var host = await ElsaSlaSqlHost.StartAsync(database.ConnectionString);
        var graph = await BuildAndRegisterWorkflowAsync<ReviewSlaWorkflow>(host.Services);
        var first = await RunWorkflowAsync(host.Services, graph, CreateOptions("sla-short", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        var second = await RunWorkflowAsync(host.Services, graph, CreateOptions("sla-long", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1)));
        Assert.NotEqual(first.WorkflowState.Id, second.WorkflowState.Id);
        Assert.Equal(WorkflowSubStatus.Suspended, first.SubStatus);
        Assert.Equal(WorkflowSubStatus.Suspended, second.SubStatus);

        var firstFinished = await WaitForInstanceAsync(host.Services, first.WorkflowState.Id, instance => instance.Status == WorkflowStatus.Finished);
        Assert.Equal(1, ReadIntOutput(firstFinished.WorkflowState.Output["ReminderCount"]));
        Assert.Equal(1, ReadIntOutput(firstFinished.WorkflowState.Output["EscalationCount"]));
        var secondPending = await GetInstanceAsync(host.Services, second.WorkflowState.Id);
        Assert.Equal(WorkflowStatus.Running, secondPending.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, secondPending.SubStatus);
        Assert.Empty(secondPending.WorkflowState.Output);
        Assert.Single(await FindBookmarksAsync(host.Services, second.WorkflowState.Id));

        var secondFinished = await WaitForInstanceAsync(host.Services, second.WorkflowState.Id, instance => instance.Status == WorkflowStatus.Finished);
        Assert.Equal(1, ReadIntOutput(secondFinished.WorkflowState.Output["ReminderCount"]));
        Assert.Equal(1, ReadIntOutput(secondFinished.WorkflowState.Output["EscalationCount"]));
        var actions = host.Services.GetRequiredService<IReviewSlaActionService>().Actions;
        Assert.Equal(4, actions.Count);
        Assert.Equal(2, actions.Count(action => action.Kind == ReviewSlaActionKind.Reminder));
        Assert.Equal(2, actions.Count(action => action.Kind == ReviewSlaActionKind.Escalation));
        Assert.Contains(actions, action => action.ReviewKey == "sla-short");
        Assert.Contains(actions, action => action.ReviewKey == "sla-long");
    }

    [Fact]
    [Trait("Category", "SlaTimer")]
    public async Task ReviewSlaActionService_IsIdempotentForSameCommand_AndRejectsConflictingKeyReuse()
    {
        var service = new InMemoryReviewSlaActionService();
        var command = new ReviewSlaActionCommand("Reminder:task-1:1", "task-1", ReviewSlaActionKind.Reminder, 1);

        var first = await service.SendReminderAsync(command, CancellationToken.None);
        var duplicate = await service.SendReminderAsync(command, CancellationToken.None);

        Assert.Equal(ReviewSlaOperationStatus.Applied, first.Status);
        Assert.Equal(ReviewSlaOperationStatus.AlreadyApplied, duplicate.Status);
        Assert.Single(service.Actions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendReminderAsync(
            command with { ReviewKey = "different-task" }, CancellationToken.None));
        Assert.Single(service.Actions);
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable(SqlServerConnectionVariable)
        ?? throw new InvalidOperationException($"{SqlServerConnectionVariable} is required for SQL timer integration tests.");

    internal static RunWorkflowOptions CreateOptions(string reviewKey, TimeSpan initialDelay, TimeSpan reminderDelay) => new()
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = "DPC-10-ME-0001",
            ["ReviewKey"] = reviewKey,
            ["InitialDelay"] = initialDelay,
            ["ReminderDelay"] = reminderDelay
        }
    };

    internal static async Task<WorkflowGraph> BuildAndRegisterWorkflowAsync<TWorkflow>(IServiceProvider services)
        where TWorkflow : WorkflowBase, new()
    {
        var workflow = await services.GetRequiredService<IWorkflowBuilderFactory>()
            .CreateBuilder()
            .BuildWorkflowAsync<TWorkflow>();
        await services.GetRequiredService<IWorkflowRegistry>().RegisterAsync(workflow);
        return await services.GetRequiredService<IWorkflowGraphBuilder>().BuildAsync(workflow);
    }

    internal static async Task<SlaWorkflowRun> RunWorkflowAsync(
        IServiceProvider services,
        WorkflowGraph graph,
        RunWorkflowOptions options)
    {
        await services.GetRequiredService<IWorkflowDefinitionStorePopulator>().PopulateStoreAsync();
        var definition = await services.GetRequiredService<IWorkflowDefinitionStore>()
            .FindAsync(new WorkflowDefinitionFilter { DefinitionId = graph.Workflow.Identity.DefinitionId })
            ?? throw new Xunit.Sdk.XunitException($"Code-first definition '{graph.Workflow.Identity.DefinitionId}' was not populated into Elsa's definition store.");
        var client = await services.GetRequiredService<IWorkflowRuntime>().CreateClientAsync();
        var response = await client.CreateAndRunInstanceAsync(new CreateAndRunWorkflowInstanceRequest
        {
            WorkflowDefinitionHandle = WorkflowDefinitionHandle.ByDefinitionVersionId(definition.Id),
            Input = options.Input,
            IncludeWorkflowOutput = true
        });
        var workflowState = await client.ExportStateAsync();
        return new SlaWorkflowRun(response, workflowState);
    }

    internal static async Task<WorkflowInstance> GetInstanceAsync(IServiceProvider services, string workflowInstanceId) =>
        await services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
        ?? throw new Xunit.Sdk.XunitException($"Workflow instance '{workflowInstanceId}' was not found.");

    internal static async Task<WorkflowInstance> WaitForInstanceAsync(
        IServiceProvider services,
        string workflowInstanceId,
        Func<WorkflowInstance, bool> predicate)
    {
        var stopAt = DateTimeOffset.UtcNow.AddSeconds(20);
        WorkflowInstance? instance = null;
        while (DateTimeOffset.UtcNow < stopAt)
        {
            instance = await GetInstanceAsync(services, workflowInstanceId);
            if (predicate(instance))
                return instance;
            if (instance.Status == WorkflowStatus.Finished && instance.SubStatus == WorkflowSubStatus.Faulted)
                throw new Xunit.Sdk.XunitException(
                    $"Workflow '{workflowInstanceId}' faulted while waiting for its expected state. " +
                    $"Incidents: {DescribeIncidents(instance)}");
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new Xunit.Sdk.XunitException(
            $"Workflow '{workflowInstanceId}' did not reach the expected state. Last state: {instance?.Status}/{instance?.SubStatus}. " +
            $"Incidents: {(instance is null ? "none" : DescribeIncidents(instance))}");
    }

    private static string DescribeIncidents(WorkflowInstance instance) => string.Join(" | ", instance.WorkflowState.Incidents.Select(incident =>
        $"{incident.GetType().Name}: " + string.Join(", ", incident.GetType().GetProperties()
            .Where(property => property.Name is "Message" or "ActivityId" or "ActivityNodeId" or "Exception")
            .Select(property => $"{property.Name}={property.GetValue(incident)}"))));

    internal static async Task<StoredBookmark[]> FindBookmarksAsync(IServiceProvider services, string workflowInstanceId)
    {
        var page = await services.GetRequiredService<IBookmarkStore>().FindManyAsync(
            new BookmarkFilter { WorkflowInstanceId = workflowInstanceId },
            PageArgs.FromRange(0, 100));
        return page.Items.ToArray();
    }

    internal static DateTimeOffset ReadResumeAt(object? payload)
    {
        if (payload is DelayPayload typed)
            return typed.ResumeAt;
        if (payload is JsonElement json)
            return json.EnumerateObject().Single(item => string.Equals(item.Name, "resumeAt", StringComparison.OrdinalIgnoreCase)).Value.GetDateTimeOffset();
        if (payload is IDictionary<string, object?> dictionary)
        {
            var raw = dictionary.Single(item => string.Equals(item.Key, "resumeAt", StringComparison.OrdinalIgnoreCase)).Value;
            return raw switch
            {
                DateTimeOffset dateTimeOffset => dateTimeOffset,
                DateTime dateTime => new DateTimeOffset(dateTime),
                JsonElement value => value.GetDateTimeOffset(),
                _ => DateTimeOffset.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture)
            };
        }

        throw new Xunit.Sdk.XunitException($"Unexpected Delay bookmark payload: {payload?.GetType().FullName ?? "null"}.");
    }

    internal static int ReadIntOutput(object? value) => value switch
    {
        int integer => integer,
        JsonElement json => json.GetInt32(),
        _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
    };

    internal static bool ReadBooleanOutput(object? value) => value switch
    {
        bool boolean => boolean,
        JsonElement json => json.GetBoolean(),
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
    };
}

internal sealed record SlaWorkflowRun(RunWorkflowInstanceResponse Response, WorkflowState WorkflowState)
{
    public WorkflowSubStatus SubStatus { get; init; } = Response.SubStatus;
}

internal sealed class SlaTimerFactAttribute : FactAttribute
{
    public SlaTimerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ELSALAB_SQLSERVER_CONNECTION_STRING")))
            Skip = "Set ELSALAB_SQLSERVER_CONNECTION_STRING to run SQL Server SLA timer tests.";
    }
}

internal sealed class ElsaSlaSqlHost : IAsyncDisposable
{
    private ElsaSlaSqlHost(IHost host) => Host = host;

    public IHost Host { get; }
    public IServiceProvider Services => Host.Services;

    public static async Task<ElsaSlaSqlHost> StartAsync(string connectionString)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
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
            return new ElsaSlaSqlHost(host);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Host.StopAsync(timeout.Token);
        // The local scheduler's timer callback can finish just after the workflow row is committed.
        // Keep the provider alive briefly so that callback scopes can unwind before disposal.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Host.Dispose();
    }
}
