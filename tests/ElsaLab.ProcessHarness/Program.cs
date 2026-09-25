using System.Dynamic;
using System.Globalization;
using System.Text.Json;
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
using Elsa.Workflows.Runtime.Messages;
using Elsa.Workflows.State;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class Program
{
    private const string ConnectionStringEnvironmentVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";
    private const string JsonMessagePrefix = "ELSA_PROCESS_RESULT=";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
                throw new ArgumentException("Expected a command: suspend, suspend-two, hold, inspect, resume, or resume-unregistered.");

            var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)
                ?? throw new InvalidOperationException($"Set {ConnectionStringEnvironmentVariable} before launching the process harness.");

            return args[0] switch
            {
                "suspend" => await SuspendAsync(connectionString, hold: false),
                "hold" => await SuspendAsync(connectionString, hold: true),
                "suspend-two" => await SuspendTwoAsync(connectionString),
                "inspect" when args.Length == 3 => await InspectAsync(connectionString, args[1], args[2]),
                "resume" when args.Length == 3 => await ResumeAsync(connectionString, args[1], args[2], registerWorkflow: true),
                "resume-other" when args.Length == 5 => await ResumeOtherAsync(connectionString, args[1], args[2], args[3], args[4]),
                "resume-unregistered" when args.Length == 3 => await ResumeAsync(connectionString, args[1], args[2], registerWorkflow: false),
                _ when args[0].StartsWith("versioning-", StringComparison.Ordinal) => await WorkflowVersioningProcessHarness.RunAsync(connectionString, args),
                _ when args[0].StartsWith("sla-", StringComparison.Ordinal) => await SlaTimerProcessHarness.RunAsync(connectionString, args),
                _ when args[0].StartsWith("failure-", StringComparison.Ordinal) => await FailureProcessHarness.RunAsync(connectionString, args),
                _ => throw new ArgumentException($"Invalid arguments for command '{args[0]}'.")
            };
        }
        catch (Exception exception)
        {
            // Never print configuration values, which may include SQL credentials.
            Console.Error.WriteLine($"{exception.GetType().FullName}: {exception.Message}");
            return 2;
        }
    }

    private static async Task<int> SuspendAsync(string connectionString, bool hold)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        var (workflow, graph) = await BuildAndRegisterWorkflowAsync(host.Services);
        var runResult = await host.Services.GetRequiredService<IWorkflowRunner>().RunAsync(
            graph,
            CreateStartOptions("DPC-10-ME-0001", 2, "discipline-review"));

        EnsureSuspended(runResult);
        var workflowInstanceId = runResult.WorkflowState.Id;
        var bookmarkId = AssertSingle(runResult.WorkflowState.Bookmarks).Id;
        var persisted = await LoadSnapshotAsync(host.Services, workflowInstanceId, bookmarkId);

        WriteMessage(new
        {
            command = hold ? "hold" : "suspend",
            processId = Environment.ProcessId,
            definitionId = workflow.Identity.DefinitionId,
            workflowInstanceId,
            bookmarkId,
            bookmarkName = persisted.Bookmark.Name,
            bookmarkHash = persisted.Bookmark.Hash,
            activityInstanceId = persisted.Bookmark.ActivityInstanceId,
            status = persisted.Instance.Status.ToString(),
            subStatus = persisted.Instance.SubStatus.ToString(),
            bookmarkExists = true,
            payload = persisted.Bookmark.Payload,
            incidentCount = persisted.Instance.WorkflowState.Incidents.Count
        });

        if (hold)
        {
            // The parent kills this process after another process confirms the suspended state is in SQL.
            await Console.In.ReadLineAsync();
            return 0;
        }

        await StopHostAsync(host);
        return 0;
    }

    private static async Task<int> SuspendTwoAsync(string connectionString)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        var (_, graph) = await BuildAndRegisterWorkflowAsync(host.Services);
        var runner = host.Services.GetRequiredService<IWorkflowRunner>();
        var first = await runner.RunAsync(graph, CreateStartOptions("DPC-10-ME-0001", 2, "review-A"));
        var second = await runner.RunAsync(graph, CreateStartOptions("DPC-10-ME-0002", 5, "review-B"));
        EnsureSuspended(first);
        EnsureSuspended(second);

        var firstSnapshot = await LoadSnapshotAsync(host.Services, first.WorkflowState.Id, AssertSingle(first.WorkflowState.Bookmarks).Id);
        var secondSnapshot = await LoadSnapshotAsync(host.Services, second.WorkflowState.Id, AssertSingle(second.WorkflowState.Bookmarks).Id);
        WriteMessage(new
        {
            command = "suspend-two",
            processId = Environment.ProcessId,
            workflows = new[] { ToSuspendedMessage(firstSnapshot), ToSuspendedMessage(secondSnapshot) }
        });

        await StopHostAsync(host);
        return 0;
    }

    private static async Task<int> InspectAsync(string connectionString, string workflowInstanceId, string bookmarkId)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        var snapshot = await LoadSnapshotAsync(host.Services, workflowInstanceId, bookmarkId);
        WriteMessage(new
        {
            command = "inspect",
            processId = Environment.ProcessId,
            workflowInstanceId = snapshot.Instance.Id,
            bookmarkId = snapshot.Bookmark.Id,
            bookmarkName = snapshot.Bookmark.Name,
            bookmarkHash = snapshot.Bookmark.Hash,
            activityInstanceId = snapshot.Bookmark.ActivityInstanceId,
            bookmarkWorkflowInstanceId = snapshot.Bookmark.WorkflowInstanceId,
            metadata = snapshot.Bookmark.Metadata,
            payload = snapshot.Bookmark.Payload,
            status = snapshot.Instance.Status.ToString(),
            subStatus = snapshot.Instance.SubStatus.ToString(),
            bookmarkExists = true,
            inputCount = snapshot.Instance.WorkflowState.Input.Count,
            outputCount = snapshot.Instance.WorkflowState.Output.Count,
            incidentCount = snapshot.Instance.WorkflowState.Incidents.Count
        });

        await StopHostAsync(host);
        return 0;
    }

    private static async Task<int> ResumeAsync(string connectionString, string workflowInstanceId, string bookmarkId, bool registerWorkflow)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        if (registerWorkflow)
            await BuildAndRegisterWorkflowAsync(host.Services);

        string? exceptionType = null;
        RunWorkflowInstanceResponse? response = null;
        try
        {
            response = await host.Services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
                bookmarkId,
                new Dictionary<string, object>(),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            exceptionType = exception.GetType().FullName;
        }

        var instance = await host.Services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
            ?? throw new InvalidOperationException("Workflow instance was missing after the resume attempt.");
        var bookmark = await host.Services.GetRequiredService<IBookmarkStore>()
            .FindAsync(new BookmarkFilter { BookmarkId = bookmarkId });

        WriteMessage(new
        {
            command = registerWorkflow ? "resume" : "resume-unregistered",
            processId = Environment.ProcessId,
            explicitWorkflowRegistration = registerWorkflow,
            workflowInstanceId = instance.Id,
            bookmarkId,
            succeeded = exceptionType == null && response?.Status == WorkflowStatus.Finished && response.SubStatus == WorkflowSubStatus.Finished,
            resumerReturnedResponse = response != null,
            exceptionType,
            responseWorkflowInstanceId = response?.WorkflowInstanceId,
            responseStatus = response?.Status.ToString(),
            responseSubStatus = response?.SubStatus.ToString(),
            responseIncidentCount = response?.Incidents.Count,
            status = instance.Status.ToString(),
            subStatus = instance.SubStatus.ToString(),
            bookmarkExists = bookmark != null,
            outputs = instance.WorkflowState.Output,
            incidentCount = instance.WorkflowState.Incidents.Count
        });

        await StopHostAsync(host);
        return 0;
    }

    private static async Task<int> ResumeOtherAsync(
        string connectionString,
        string workflowInstanceId,
        string bookmarkId,
        string otherWorkflowInstanceId,
        string otherBookmarkId)
    {
        using var host = await CreateAndStartHostAsync(connectionString);
        await BuildAndRegisterWorkflowAsync(host.Services);

        var response = await host.Services.GetRequiredService<IWorkflowResumer>().ResumeAsync(
            bookmarkId,
            new Dictionary<string, object>(),
            CancellationToken.None);
        var instanceStore = host.Services.GetRequiredService<IWorkflowInstanceStore>();
        var bookmarkStore = host.Services.GetRequiredService<IBookmarkStore>();
        var finishedInstance = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
            ?? throw new InvalidOperationException("Target instance was missing after resume.");
        var otherInstance = await instanceStore.FindAsync(new WorkflowInstanceFilter { Id = otherWorkflowInstanceId })
            ?? throw new InvalidOperationException("Other instance was missing after target resume.");
        var targetBookmark = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = bookmarkId });
        var otherBookmark = await bookmarkStore.FindAsync(new BookmarkFilter { BookmarkId = otherBookmarkId });

        WriteMessage(new
        {
            command = "resume-other",
            processId = Environment.ProcessId,
            workflowInstanceId = finishedInstance.Id,
            responseWorkflowInstanceId = response?.WorkflowInstanceId,
            succeeded = response?.Status == WorkflowStatus.Finished && response.SubStatus == WorkflowSubStatus.Finished,
            status = finishedInstance.Status.ToString(),
            subStatus = finishedInstance.SubStatus.ToString(),
            bookmarkExists = targetBookmark != null,
            outputs = finishedInstance.WorkflowState.Output,
            incidentCount = finishedInstance.WorkflowState.Incidents.Count,
            otherWorkflowInstanceId = otherInstance.Id,
            otherStatus = otherInstance.Status.ToString(),
            otherSubStatus = otherInstance.SubStatus.ToString(),
            otherBookmarkExists = otherBookmark != null,
            otherOutputsEmpty = otherInstance.WorkflowState.Output.Count == 0
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

    private static async Task<WorkflowSnapshot> LoadSnapshotAsync(IServiceProvider services, string workflowInstanceId, string bookmarkId)
    {
        var instance = await services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId })
            ?? throw new InvalidOperationException($"Workflow instance '{workflowInstanceId}' was not present in SQL.");
        var bookmark = await services.GetRequiredService<IBookmarkStore>()
            .FindAsync(new BookmarkFilter { BookmarkId = bookmarkId })
            ?? throw new InvalidOperationException($"Bookmark '{bookmarkId}' was not present in SQL.");

        return new WorkflowSnapshot(instance, new BookmarkSnapshot(
            bookmark.Id,
            bookmark.Name ?? throw new InvalidOperationException("Bookmark name was null."),
            bookmark.Hash,
            bookmark.ActivityInstanceId,
            bookmark.WorkflowInstanceId,
            bookmark.Metadata,
            ReadPayload(bookmark.Payload)));
    }

    private static object ToSuspendedMessage(WorkflowSnapshot snapshot) => new
    {
        workflowInstanceId = snapshot.Instance.Id,
        bookmarkId = snapshot.Bookmark.Id,
        bookmarkName = snapshot.Bookmark.Name,
        bookmarkHash = snapshot.Bookmark.Hash,
        activityInstanceId = snapshot.Bookmark.ActivityInstanceId,
        status = snapshot.Instance.Status.ToString(),
        subStatus = snapshot.Instance.SubStatus.ToString(),
        bookmarkExists = true,
        payload = snapshot.Bookmark.Payload
    };

    private static PayloadSnapshot ReadPayload(object? payload)
    {
        if (payload is DocumentReviewBookmarkPayload typed)
            return new(payload.GetType().FullName!, typed.DocumentNumber, typed.Revision, typed.ReviewKey);

        if (payload is IDictionary<string, object?> dictionary)
            return new(
                payload.GetType().FullName!,
                Convert.ToString(GetValue(dictionary, "documentNumber"), CultureInfo.InvariantCulture)!,
                Convert.ToInt32(GetValue(dictionary, "revision"), CultureInfo.InvariantCulture),
                Convert.ToString(GetValue(dictionary, "reviewKey"), CultureInfo.InvariantCulture)!);

        if (payload is JsonElement json)
            return new(
                payload.GetType().FullName!,
                GetProperty(json, "documentNumber").GetString()!,
                GetProperty(json, "revision").GetInt32(),
                GetProperty(json, "reviewKey").GetString()!);

        if (payload is ExpandoObject expando)
            return ReadPayload((IDictionary<string, object?>)expando);

        throw new InvalidOperationException($"Unexpected bookmark payload type: {payload?.GetType().FullName ?? "null"}.");
    }

    private static object? GetValue(IDictionary<string, object?> dictionary, string name) =>
        dictionary.First(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static JsonElement GetProperty(JsonElement json, string name) =>
        json.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void EnsureSuspended(RunWorkflowResult result)
    {
        if (result.WorkflowState.Status != WorkflowStatus.Running ||
            result.WorkflowExecutionContext.SubStatus != WorkflowSubStatus.Suspended ||
            result.WorkflowState.Bookmarks.Count != 1 ||
            result.WorkflowState.Incidents.Count != 0 ||
            result.Journal.ActivityExecutionContexts.Any(context => context.Status == ActivityStatus.Faulted) ||
            result.Journal.ActivityExecutionContexts.Any(context => context.Activity.Name == "FinalizeDocument"))
        {
            throw new InvalidOperationException("The workflow did not reach the expected clean Running/Suspended state.");
        }
    }

    private static T AssertSingle<T>(IEnumerable<T> items)
    {
        var materialized = items.Take(2).ToArray();
        return materialized.Length == 1
            ? materialized[0]
            : throw new InvalidOperationException($"Expected exactly one item, found at least {materialized.Length}.");
    }

    private static void WriteMessage(object value) =>
        Console.WriteLine(JsonMessagePrefix + JsonSerializer.Serialize(value));

    private sealed record WorkflowSnapshot(WorkflowInstance Instance, BookmarkSnapshot Bookmark);
    private sealed record BookmarkSnapshot(string Id, string Name, string Hash, string? ActivityInstanceId, string WorkflowInstanceId, IDictionary<string, string>? Metadata, PayloadSnapshot Payload);
    private sealed record PayloadSnapshot(string ClrType, string DocumentNumber, int Revision, string ReviewKey);
}
