using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace ElsaLab.Tests;

public sealed class SlaTimerProcessRestartTests
{
    private const string SqlServerConnectionVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";
    private const string JsonMessagePrefix = "ELSA_PROCESS_RESULT=";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(75);
    private readonly ITestOutputHelper _output;

    public SlaTimerProcessRestartTests(ITestOutputHelper output) => _output = output;

    [ProcessRestartFact]
    [Trait("Category", "SlaTimer")]
    [Trait("Category", "ProcessRestart")]
    [Trait("Category", "SqlServer")]
    public async Task GracefulRestartBeforeDue_RestoresTimerAndContinuesOnce()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        using var workspace = new ProcessWorkspace();

        var processA = await RunChildAsync(workspace, database.ConnectionString, "sla-start", "sla-graceful", "12000", "600");
        AssertSuspendedMessage(processA.Message, "sla-start");
        var instanceId = GetString(processA.Message, "workflowInstanceId");
        var bookmarkId = GetString(processA.Message, "bookmarkId");
        var resumeAt = ReadResumeAt(GetProperty(processA.Message, "bookmarkPayload"));
        Assert.True(resumeAt > DateTimeOffset.UtcNow);
        _output.WriteLine($"Process A PID {processA.ProcessId} exited gracefully with a committed Delay bookmark due at {resumeAt:O}.");

        var processB = await RunChildAsync(workspace, database.ConnectionString, "sla-inspect", instanceId);
        Assert.Equal("Running", GetString(processB.Message, "status"));
        Assert.Equal("Suspended", GetString(processB.Message, "subStatus"));
        Assert.Equal(1, GetInt32(processB.Message, "bookmarkCount"));
        Assert.Equal(bookmarkId, GetString(GetProperty(processB.Message, "bookmarks").EnumerateArray().Single(), "id"));
        Assert.True(resumeAt > DateTimeOffset.UtcNow, "The inspect-only process should have observed the timer before its due time.");
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        _output.WriteLine($"Process B PID {processB.ProcessId} loaded the pending timer before its deadline and exited without consuming it.");

        var processC = await RunChildAsync(workspace, database.ConnectionString, "sla-recover", instanceId, "45");
        Assert.Equal(instanceId, GetString(processC.Message, "workflowInstanceId"));
        AssertFinishedSla(processC.Message);
        AssertActions(processC.Message, "sla-graceful");
        Assert.NotEqual(processB.ProcessId, processC.ProcessId);
        Assert.NotEqual(processA.ProcessId, processC.ProcessId);
        _output.WriteLine($"Process C PID {processC.ProcessId} rebuilt the local schedule and completed reminder/escalation after the due time.");

        var processD = await RunChildAsync(workspace, database.ConnectionString, "sla-inspect", instanceId);
        Assert.Equal("Finished", GetString(processD.Message, "status"));
        Assert.Equal("Finished", GetString(processD.Message, "subStatus"));
        Assert.Equal(0, GetInt32(processD.Message, "bookmarkCount"));
        Assert.Equal(1, GetInt32(GetProperty(processD.Message, "outputs"), "ReminderCount"));
        Assert.Equal(1, GetInt32(GetProperty(processD.Message, "outputs"), "EscalationCount"));
        Assert.Equal(0, GetInt32(processD.Message, "actionCount"));
        Assert.NotEqual(processC.ProcessId, processD.ProcessId);
        _output.WriteLine($"Process D PID {processD.ProcessId} reloaded the finished instance with unchanged one-reminder/one-escalation outputs and no timer bookmark.");
    }

    [ProcessRestartFact]
    [Trait("Category", "SlaTimer")]
    [Trait("Category", "ProcessRestart")]
    [Trait("Category", "SqlServer")]
    public async Task AbruptKillAfterSqlCommit_OverdueTimerIsRecoveredOnStartup()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        using var workspace = new ProcessWorkspace();
        await using var processA = StartChild(workspace, database.ConnectionString, "sla-hold", "sla-abrupt-overdue", "7000", "500");

        var suspended = await processA.WaitForMessageAsync();
        AssertSuspendedMessage(suspended, "sla-hold");
        var instanceId = GetString(suspended, "workflowInstanceId");
        var bookmarkId = GetString(suspended, "bookmarkId");
        var dueAt = ReadResumeAt(GetProperty(suspended, "bookmarkPayload"));

        // This provider has no scheduling module. It proves the workflow and Delay bookmark reached SQL before termination.
        await using (var readOnlyHost = await ElsaSqlTestHost.CreateAsync(database.ConnectionString))
        {
            var instance = await readOnlyHost.Services.GetRequiredService<IWorkflowInstanceStore>()
                .FindAsync(new WorkflowInstanceFilter { Id = instanceId });
            Assert.NotNull(instance);
            Assert.Equal("Running", instance.Status.ToString());
            Assert.Equal("Suspended", instance.SubStatus.ToString());
            Assert.True(dueAt > DateTimeOffset.UtcNow, "Process A must be killed only after persistence and before the timer is due.");

            var managementFactory = readOnlyHost.Services.GetRequiredService<IDbContextFactory<ManagementElsaDbContext>>();
            await using var managementDb = await managementFactory.CreateDbContextAsync();
            Assert.Equal(1, await managementDb.WorkflowInstances.AsNoTracking().CountAsync(item => item.Id == instanceId));

            var runtimeFactory = readOnlyHost.Services.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>();
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync();
            var persistedTimerRow = await runtimeDb.Bookmarks.AsNoTracking().SingleAsync(item => item.Id == bookmarkId);
            Assert.Equal(instanceId, persistedTimerRow.WorkflowInstanceId);
            Assert.Equal("Elsa.Delay", persistedTimerRow.Name);
        }

        await processA.KillTreeAndWaitAsync();
        Assert.True(processA.HasExited);
        _output.WriteLine($"Process A PID {processA.ProcessId} was killed after SQL confirmed both rows, with the timer still pending.");

        var offlineUntil = dueAt.AddMilliseconds(750);
        var remaining = offlineUntil - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        var processB = await RunChildAsync(workspace, database.ConnectionString, "sla-recover", instanceId, "45");
        var hostStartedAt = GetDateTimeOffset(processB.Message, "hostStartedAt");
        Assert.True(dueAt < hostStartedAt, "The new process must start after the persisted timer is overdue.");
        AssertFinishedSla(processB.Message);
        AssertActions(processB.Message, "sla-abrupt-overdue");
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        var catchUpTime = GetDateTimeOffset(processB.Message, "completedAt") - hostStartedAt;
        Assert.True(catchUpTime < TimeSpan.FromSeconds(20), $"Overdue timer did not catch up promptly; completion took {catchUpTime}.");
        _output.WriteLine($"Process B PID {processB.ProcessId} started after the due time and completed from the persisted Delay bookmark in {catchUpTime}.");

        await using var verifyHost = await ElsaSqlTestHost.CreateAsync(database.ConnectionString);
        var final = await verifyHost.Services.GetRequiredService<IWorkflowInstanceStore>()
            .FindAsync(new WorkflowInstanceFilter { Id = instanceId });
        Assert.NotNull(final);
        Assert.Equal("Finished", final.Status.ToString());
        Assert.Equal("Finished", final.SubStatus.ToString());
        Assert.Equal(1, ReviewSlaTimerTests.ReadIntOutput(final.WorkflowState.Output["ReminderCount"]));
        Assert.Equal(1, ReviewSlaTimerTests.ReadIntOutput(final.WorkflowState.Output["EscalationCount"]));
        Assert.Empty(final.WorkflowState.Incidents);
        Assert.Empty(await ReviewSlaTimerTests.FindBookmarksAsync(verifyHost.Services, instanceId));

        var runtimeDbFactory = verifyHost.Services.GetRequiredService<IDbContextFactory<RuntimeElsaDbContext>>();
        await using var finalRuntimeDb = await runtimeDbFactory.CreateDbContextAsync();
        Assert.Equal(0, await finalRuntimeDb.Bookmarks.AsNoTracking().CountAsync(item => item.WorkflowInstanceId == instanceId));
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable(SqlServerConnectionVariable)
        ?? throw new InvalidOperationException($"{SqlServerConnectionVariable} is required for SQL timer process tests.");

    private static async Task<ProcessResult> RunChildAsync(ProcessWorkspace workspace, string connectionString, params string[] arguments)
    {
        await using var child = StartChild(workspace, connectionString, arguments);
        var message = await child.WaitForMessageAsync();
        var exitCode = await child.WaitForExitAsync();
        if (exitCode != 0)
            throw new Xunit.Sdk.XunitException($"Child process exited with {exitCode}.\n{child.Diagnostics}");
        return new ProcessResult(child.ProcessId, exitCode, message);
    }

    private static HarnessProcess StartChild(ProcessWorkspace workspace, string connectionString, params string[] arguments)
    {
        var harnessAssembly = FindHarnessAssembly();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? "Debug";
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace.Path,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(harnessAssembly);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[SqlServerConnectionVariable] = connectionString;
        return HarnessProcess.Start(startInfo);
    }

    private static string FindHarnessAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name ?? throw new InvalidOperationException("Could not determine test build configuration.");
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate the ElsaLab repository root from the test output directory.");

        var assembly = Path.Combine(directory.FullName, "tests", "ElsaLab.ProcessHarness", "bin", configuration, "net10.0", "ElsaLab.ProcessHarness.dll");
        return File.Exists(assembly) ? assembly : throw new FileNotFoundException("The process harness assembly was not built.", assembly);
    }

    private static void AssertSuspendedMessage(JsonElement message, string expectedCommand)
    {
        Assert.Equal(expectedCommand, GetString(message, "command"));
        Assert.Equal("Running", GetString(message, "status"));
        Assert.Equal("Suspended", GetString(message, "subStatus"));
        Assert.Equal(1, GetInt32(message, "bookmarkCount"));
        Assert.Equal(0, GetInt32(message, "incidentCount"));
        Assert.Equal("Elsa.Delay", GetString(message, "bookmarkName"));
    }

    private static void AssertFinishedSla(JsonElement message)
    {
        Assert.Equal("Finished", GetString(message, "status"));
        Assert.Equal("Finished", GetString(message, "subStatus"));
        Assert.Equal(0, GetInt32(message, "bookmarkCount"));
        Assert.Equal(0, GetInt32(message, "incidentCount"));
        var outputs = GetProperty(message, "outputs");
        Assert.Equal(1, GetInt32(outputs, "ReminderCount"));
        Assert.Equal(1, GetInt32(outputs, "EscalationCount"));
        Assert.True(GetBoolean(outputs, "Escalated"));
        Assert.Equal("Escalated", GetString(outputs, "FinalSlaStatus"));
        Assert.Equal("DPC-10-ME-0001", GetString(outputs, "FinalDocumentNumber"));
        Assert.False(string.IsNullOrWhiteSpace(GetString(outputs, "FinalReviewKey")));
    }

    private static void AssertActions(JsonElement message, string reviewKey)
    {
        var actions = GetProperty(message, "actions").EnumerateArray().ToArray();
        Assert.Equal(2, actions.Length);
        Assert.Equal($"Reminder:{reviewKey}:1", GetString(actions[0], "OperationId"));
        Assert.Equal(reviewKey, GetString(actions[0], "ReviewKey"));
        Assert.Equal(1, GetInt32(actions[0], "ReminderNumber"));
        Assert.Equal($"Escalation:{reviewKey}", GetString(actions[1], "OperationId"));
        Assert.Equal(reviewKey, GetString(actions[1], "ReviewKey"));
    }

    private static DateTimeOffset ReadResumeAt(JsonElement payload)
    {
        var value = GetProperty(payload, "ResumeAt");
        return value.GetDateTimeOffset();
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string GetString(JsonElement element, string name) => GetProperty(element, name).GetString()!;
    private static int GetInt32(JsonElement element, string name) => GetProperty(element, name).GetInt32();
    private static bool GetBoolean(JsonElement element, string name) => GetProperty(element, name).GetBoolean();
    private static DateTimeOffset GetDateTimeOffset(JsonElement element, string name) => GetProperty(element, name).GetDateTimeOffset();

    private sealed record ProcessResult(int ProcessId, int ExitCode, JsonElement Message);

    private sealed class ProcessWorkspace : IDisposable
    {
        public ProcessWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"elsalab-sla-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class HarnessProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource<JsonElement> _message = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();

        private HarnessProcess(Process process) => _process = process;

        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;
        public string Diagnostics
        {
            get
            {
                lock (_gate)
                    return $"stdout:\n{_stdout}\nstderr:\n{_stderr}";
            }
        }

        public static HarnessProcess Start(ProcessStartInfo startInfo)
        {
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var harness = new HarnessProcess(process);
            if (!process.Start())
                throw new InvalidOperationException("Could not start the ElsaLab process harness.");
            process.OutputDataReceived += harness.OnOutput;
            process.ErrorDataReceived += harness.OnError;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return harness;
        }

        public async Task<JsonElement> WaitForMessageAsync()
        {
            try
            {
                return await _message.Task.WaitAsync(ChildTimeout);
            }
            catch (TimeoutException)
            {
                await KillTreeAndWaitAsync();
                throw new Xunit.Sdk.XunitException($"Child did not emit its result within {ChildTimeout}.\n{Diagnostics}");
            }
        }

        public async Task<int> WaitForExitAsync()
        {
            try
            {
                await _process.WaitForExitAsync().WaitAsync(ChildTimeout);
                return _process.ExitCode;
            }
            catch (TimeoutException)
            {
                await KillTreeAndWaitAsync();
                throw new Xunit.Sdk.XunitException($"Child did not exit within {ChildTimeout}.\n{Diagnostics}");
            }
        }

        public async Task KillTreeAndWaitAsync()
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited after the check.
                }
            }

            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                await KillTreeAndWaitAsync();
            _process.Dispose();
        }

        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;
            lock (_gate)
                _stdout.AppendLine(args.Data);
            if (!args.Data.StartsWith(JsonMessagePrefix, StringComparison.Ordinal))
                return;

            try
            {
                var json = JsonDocument.Parse(args.Data[JsonMessagePrefix.Length..]).RootElement.Clone();
                _message.TrySetResult(json);
            }
            catch (Exception exception)
            {
                _message.TrySetException(new Xunit.Sdk.XunitException($"Child emitted malformed JSON: {exception.Message}\n{Diagnostics}"));
            }
        }

        private void OnError(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;
            lock (_gate)
                _stderr.AppendLine(args.Data);
        }
    }
}
