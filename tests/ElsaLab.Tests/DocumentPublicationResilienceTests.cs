using Elsa.Extensions;
using Elsa.Common;
using Elsa.Resilience;
using Elsa.Resilience.Entities;
using Elsa.Resilience.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Options;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Filters;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ElsaLab.Tests;

public class DocumentPublicationResilienceTests
{
    [Fact]
    [Trait("Category", "Resilience")]
    public async Task TransientFailures_AreRetriedAndSucceedWithRecordedAttempts()
    {
        var service = new ScriptedPublicationService(transientFailures: 2);
        using var provider = CreateServices(service);
        using var scope = provider.CreateScope();
        var result = await RunAsync(scope.ServiceProvider, new DocumentPublicationWorkflow());

        Assert.Equal(3, service.CallCount);
        Assert.Single(service.AppliedOperations);
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);
        Assert.Empty(result.WorkflowState.Incidents);
        Assert.Single(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "AfterPublication");
        var activityContext = Assert.Single(result.Journal.ActivityExecutionContexts,
            context => context.Activity is PublishDocumentActivity);
        Assert.Equal(ActivityStatus.Completed, activityContext.Status);
        Assert.Equal("Applied", result.WorkflowState.Output["PublicationStatus"]);
        Assert.Equal("Client", result.WorkflowState.Output["PublishedDestination"]);

        var attempts = await scope.ServiceProvider.GetRequiredService<IRetryAttemptReader>()
            .ReadAttemptsAsync(activityContext.Id);
        Assert.Equal(2, attempts.Items.Count);
        Assert.Collection(attempts.Items.OrderBy(attempt => attempt.AttemptNumber),
            first => AssertRetry(first, 0, activityContext, "temporary publication outage"),
            second => AssertRetry(second, 1, activityContext, "temporary publication outage"));
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task PermanentFailure_IsNotRetried_AndFaultStrategyStopsTheWorkflow()
    {
        var service = new ScriptedPublicationService(permanentFailure: true);
        using var provider = CreateServices(service);
        using var scope = provider.CreateScope();
        var result = await RunAsync(scope.ServiceProvider, new DocumentPublicationWorkflow());

        Assert.Equal(1, service.CallCount);
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Faulted, result.WorkflowExecutionContext.SubStatus);
        Assert.DoesNotContain(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "AfterPublication");
        var activityContext = Assert.Single(result.Journal.ActivityExecutionContexts,
            context => context.Activity is PublishDocumentActivity);
        Assert.Equal(ActivityStatus.Faulted, activityContext.Status);
        var incident = Assert.Single(result.WorkflowState.Incidents);
        Assert.Equal(activityContext.Activity.Id, incident.ActivityId);
        Assert.Contains(nameof(DocumentValidationException), incident.Exception!.Type.ToString(), StringComparison.Ordinal);
        Assert.Equal("The revision is not eligible for publication.", incident.Message);
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<IRetryAttemptReader>()
            .ReadAttemptsAsync(activityContext.Id)).Items);
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task TransientFailuresAfterRetryLimit_ProduceFaultIncidentAndDoNotContinue()
    {
        var service = new ScriptedPublicationService(alwaysTransient: true);
        using var provider = CreateServices(service);
        using var scope = provider.CreateScope();
        var result = await RunAsync(scope.ServiceProvider, new DocumentPublicationWorkflow());

        Assert.Equal(3, service.CallCount); // one initial call plus two configured retries.
        Assert.Empty(service.AppliedOperations);
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Faulted, result.WorkflowExecutionContext.SubStatus);
        Assert.DoesNotContain(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "AfterPublication");
        var activityContext = Assert.Single(result.Journal.ActivityExecutionContexts,
            context => context.Activity is PublishDocumentActivity);
        Assert.Equal(ActivityStatus.Faulted, activityContext.Status);
        var incident = Assert.Single(result.WorkflowState.Incidents);
        Assert.Equal(activityContext.Activity.Id, incident.ActivityId);
        Assert.Equal("temporary publication outage", incident.Message);

        // Elsa 3.8.4 records retry history after the resilience pipeline returns successfully.
        // Exhaustion throws before that recorder call, so no durable retry-attempt record is emitted.
        Assert.Empty((await scope.ServiceProvider.GetRequiredService<IRetryAttemptReader>()
            .ReadAttemptsAsync(activityContext.Id)).Items);
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task ContinueWithIncidentsStrategy_ContinuesAndKeepsTheIncident()
    {
        var service = new ScriptedPublicationService(permanentFailure: true);
        using var provider = CreateServices(service);
        using var scope = provider.CreateScope();
        var result = await RunAsync(scope.ServiceProvider, new DocumentPublicationContinueWithIncidentsWorkflow());

        Assert.Equal(1, service.CallCount);
        Assert.Equal(WorkflowStatus.Running, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Suspended, result.WorkflowExecutionContext.SubStatus);
        Assert.DoesNotContain(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "AfterPublication");
        Assert.Single(result.WorkflowState.Incidents);
        Assert.Empty(result.WorkflowState.Bookmarks);
        Assert.DoesNotContain(result.WorkflowState.Output.Keys, key => key == "PublicationStatus");
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task ApplyThenThrow_RetryUsesSameOperationId_AndDoesNotRepeatSideEffect()
    {
        var service = new ScriptedPublicationService(applyThenThrow: true);
        using var provider = CreateServices(service);
        using var scope = provider.CreateScope();
        var result = await RunAsync(scope.ServiceProvider, new DocumentPublicationWorkflow());

        Assert.Equal(2, service.CallCount);
        Assert.Single(service.AppliedOperations);
        Assert.Equal("Publish:DPC-10-ME-0001:R2", Assert.Single(service.AppliedOperations).OperationId);
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Finished, result.WorkflowExecutionContext.SubStatus);
        Assert.Equal("AlreadyApplied", result.WorkflowState.Output["PublicationStatus"]);
        Assert.Single(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "AfterPublication");
        var activityContext = Assert.Single(result.Journal.ActivityExecutionContexts,
            context => context.Activity is PublishDocumentActivity);
        Assert.Single((await scope.ServiceProvider.GetRequiredService<IRetryAttemptReader>()
            .ReadAttemptsAsync(activityContext.Id)).Items);
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task ReusingOperationIdForDifferentCommand_IsRejected()
    {
        var service = new InMemoryDocumentPublicationService();
        var first = new DocumentPublicationCommand("Publish:DOC100:R2", "DOC100", 2, "Client");
        await service.PublishAsync(first, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(
            first with { Destination = "Vendor" }, CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Resilience")]
    public async Task CancellationIsPropagatedAndIsNotRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var service = new ScriptedPublicationService(cancelOnFirstCall: cancellation);
        using var provider = CreateServices(service);
        using var scope = provider.CreateScope();

        var runTask = RunAsync(scope.ServiceProvider, new DocumentPublicationWorkflow(), cancellation.Token);
        await service.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, service.CallCount);
        Assert.Equal(cancellation.Token, service.ReceivedCancellationToken);
        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Equal(WorkflowSubStatus.Cancelled, result.WorkflowExecutionContext.SubStatus);
        Assert.Equal(ActivityStatus.Faulted, Assert.Single(result.Journal.ActivityExecutionContexts,
            context => context.Activity is PublishDocumentActivity).Status);
        Assert.DoesNotContain(result.Journal.ActivityExecutionContexts, context => context.Activity.Name == "AfterPublication");
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Resilience")]
    public async Task SuccessfulRetryHistoryAndFaultIncident_RoundTripThroughSqlPersistence()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("ELSALAB_SQLSERVER_CONNECTION_STRING")
            ?? throw new InvalidOperationException("ELSALAB_SQLSERVER_CONNECTION_STRING is required for SQL resilience tests.");
        await using var database = await SqlServerTestDatabase.CreateAsync(configuredConnectionString);

        string successWorkflowId;
        string retryActivityExecutionId;
        await using (var hostA = CreateServices(
                   new ScriptedPublicationService(transientFailures: 2),
                   addSqlPersistence: true,
                   connectionString: database.ConnectionString))
        {
            await RunMigrationsAsync(hostA);
            using var scopeA = hostA.CreateScope();
            var result = await RunAsync(scopeA.ServiceProvider, new DocumentPublicationWorkflow());
            Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
            successWorkflowId = result.WorkflowState.Id;
            retryActivityExecutionId = Assert.Single(result.Journal.ActivityExecutionContexts,
                context => context.Activity is PublishDocumentActivity).Id;
            var retryRecords = await scopeA.ServiceProvider.GetRequiredService<IRetryAttemptReader>()
                .ReadAttemptsAsync(retryActivityExecutionId);
            Assert.Equal(2, retryRecords.Items.Count);
            var persisted = await scopeA.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>()
                .FindAsync(new WorkflowInstanceFilter { Id = successWorkflowId });
            Assert.NotNull(persisted);
            Assert.Equal(WorkflowStatus.Finished, persisted.Status);
        }

        // This is a newly-created service provider. The reader reloads records via Elsa's SQL ActivityExecutionStore.
        await using (var hostB = CreateServices(
                   new InMemoryDocumentPublicationService(),
                   addSqlPersistence: true,
                   connectionString: database.ConnectionString))
        {
            await RunMigrationsAsync(hostB);
            using var scopeB = hostB.CreateScope();
            var attempts = await scopeB.ServiceProvider.GetRequiredService<IRetryAttemptReader>()
                .ReadAttemptsAsync(retryActivityExecutionId);
            Assert.Equal(2, attempts.Items.Count);
            Assert.Equal(new[] { 0, 1 }, attempts.Items.OrderBy(attempt => attempt.AttemptNumber)
                .Select(attempt => attempt.AttemptNumber));
            var persisted = await scopeB.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>()
                .FindAsync(new WorkflowInstanceFilter { Id = successWorkflowId });
            Assert.NotNull(persisted);
            Assert.Equal(WorkflowSubStatus.Finished, persisted.SubStatus);
        }

        string faultWorkflowId;
        string faultActivityId;
        await using (var hostA = CreateServices(
                   new ScriptedPublicationService(permanentFailure: true),
                   addSqlPersistence: true,
                   connectionString: database.ConnectionString))
        {
            await RunMigrationsAsync(hostA);
            using var scopeA = hostA.CreateScope();
            var result = await RunAsync(scopeA.ServiceProvider, new DocumentPublicationWorkflow());
            Assert.Equal(WorkflowSubStatus.Faulted, result.WorkflowExecutionContext.SubStatus);
            faultWorkflowId = result.WorkflowState.Id;
            var activityContext = Assert.Single(result.Journal.ActivityExecutionContexts,
                context => context.Activity is PublishDocumentActivity);
            faultActivityId = activityContext.Activity.Id;
        }

        await using (var hostB = CreateServices(
                   new InMemoryDocumentPublicationService(),
                   addSqlPersistence: true,
                   connectionString: database.ConnectionString))
        {
            await RunMigrationsAsync(hostB);
            using var scopeB = hostB.CreateScope();
            var persisted = await scopeB.ServiceProvider.GetRequiredService<IWorkflowInstanceStore>()
                .FindAsync(new WorkflowInstanceFilter { Id = faultWorkflowId });
            Assert.NotNull(persisted);
            Assert.Equal(WorkflowStatus.Finished, persisted.Status);
            Assert.Equal(WorkflowSubStatus.Faulted, persisted.SubStatus);
            var incident = Assert.Single(persisted.WorkflowState.Incidents);
            Assert.Equal(faultActivityId, incident.ActivityId);
            Assert.Equal(typeof(Exception).FullName, incident.Exception!.Type.FullName);
            Assert.Equal("The revision is not eligible for publication.", incident.Exception.Message);
        }
    }

    private static async Task RunMigrationsAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        var startupTasks = scope.ServiceProvider.GetServices<IStartupTask>().ToList();
        var managementMigration = startupTasks.OfType<RunMigrationsStartupTask<ManagementElsaDbContext>>().FirstOrDefault()
            ?? throw new Xunit.Sdk.XunitException("Elsa did not register the management migration startup task.");
        var runtimeMigration = startupTasks.OfType<RunMigrationsStartupTask<RuntimeElsaDbContext>>().FirstOrDefault()
            ?? throw new Xunit.Sdk.XunitException("Elsa did not register the runtime migration startup task.");
        await managementMigration.ExecuteAsync(CancellationToken.None);
        await runtimeMigration.ExecuteAsync(CancellationToken.None);
    }

    private static void AssertRetry(
        RetryAttemptRecord record,
        int attemptNumber,
        ActivityExecutionContext activityContext,
        string exceptionMessage)
    {
        Assert.Equal(attemptNumber, record.AttemptNumber);
        Assert.Equal(activityContext.Id, record.ActivityInstanceId);
        Assert.Equal(activityContext.Activity.Id, record.ActivityId);
        Assert.Equal(activityContext.WorkflowExecutionContext.Id, record.WorkflowInstanceId);
        Assert.Equal(TimeSpan.FromMilliseconds(5), record.RetryDelay);
        Assert.Equal(exceptionMessage, record.Details["exceptionMessage"]);
    }

    private static async Task<RunWorkflowResult> RunAsync(
        IServiceProvider services,
        WorkflowBase workflow,
        CancellationToken cancellationToken = default) =>
        await services.GetRequiredService<IWorkflowRunner>().RunAsync(
            workflow,
            CreateOptions(),
            cancellationToken);

    internal static RunWorkflowOptions CreateOptions() => new()
    {
        Input = new Dictionary<string, object>
        {
            ["DocumentNumber"] = "DPC-10-ME-0001",
            ["Revision"] = 2,
            ["Destination"] = "Client"
        }
    };

    internal static ServiceProvider CreateServices(
        IDocumentPublicationService publicationService,
        bool addSqlPersistence = false,
        string? connectionString = null,
        Type? workflowType = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Strategies:0:$type"] = nameof(DocumentPublicationRetryStrategy),
                ["Resilience:Strategies:0:Id"] = "document-publication",
                ["Resilience:Strategies:0:DisplayName"] = "Document publication transient retry",
                ["Resilience:Strategies:0:MaxRetryAttempts"] = "2",
                ["Resilience:Strategies:0:Delay"] = "00:00:00.005"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(publicationService);
        services.AddSingleton<IDocumentPublicationService>(publicationService);
        services.AddSingleton<ITransientExceptionStrategy, DocumentServiceTransientExceptionStrategy>();
        services.AddElsa(elsa =>
        {
            if (addSqlPersistence)
            {
                if (string.IsNullOrWhiteSpace(connectionString))
                    throw new ArgumentException("A connection string is required when SQL persistence is enabled.", nameof(connectionString));
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
            }

            elsa.UseResilience(resilience => resilience.AddResilienceStrategyType<DocumentPublicationRetryStrategy>());
            elsa.AddActivity<PublishDocumentActivity>();
            if (workflowType == typeof(DocumentPublicationContinueWithIncidentsWorkflow))
                elsa.AddWorkflow<DocumentPublicationContinueWithIncidentsWorkflow>();
            else
                elsa.AddWorkflow<DocumentPublicationWorkflow>();
        });
        return services.BuildServiceProvider();
    }

    private sealed class ScriptedPublicationService(
        int transientFailures = 0,
        bool permanentFailure = false,
        bool alwaysTransient = false,
        bool applyThenThrow = false,
        CancellationTokenSource? cancelOnFirstCall = null) : IDocumentPublicationService
    {
        private readonly Dictionary<string, DocumentPublicationCommand> _applied = new(StringComparer.Ordinal);
        public int CallCount { get; private set; }
        public CancellationToken ReceivedCancellationToken { get; private set; }
        public TaskCompletionSource FirstCallStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyCollection<DocumentPublicationCommand> AppliedOperations => _applied.Values.ToArray();

        public async Task<DocumentPublicationResult> PublishAsync(
            DocumentPublicationCommand command,
            CancellationToken cancellationToken)
        {
            CallCount++;
            ReceivedCancellationToken = cancellationToken;
            if (cancelOnFirstCall is not null)
            {
                FirstCallStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (permanentFailure)
                throw new DocumentValidationException("The revision is not eligible for publication.");
            if (alwaysTransient || CallCount <= transientFailures)
                throw new TransientDocumentServiceException("temporary publication outage");

            if (_applied.TryGetValue(command.OperationId, out var existing))
            {
                if (existing != command)
                    throw new InvalidOperationException("The publication operation ID was reused for a different command.");
                return new(command.OperationId, DocumentPublicationStatus.AlreadyApplied,
                    command.DocumentNumber, command.Revision, command.Destination);
            }

            _applied.Add(command.OperationId, command);
            if (applyThenThrow && CallCount == 1)
                throw new TransientDocumentServiceException("publication applied but acknowledgement was lost");

            return new(command.OperationId, DocumentPublicationStatus.Applied,
                command.DocumentNumber, command.Revision, command.Destination);
        }
    }
}
