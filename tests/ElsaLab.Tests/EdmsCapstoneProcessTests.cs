using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Elsa.Workflows;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Runtime.Filters;
using ElsaLab.Runner.Capstone;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public sealed class EdmsCapstoneProcessTests
{
    private const string ConnectionStringVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";
    private const string MessagePrefix = "EDMS_CAPSTONE_RESULT=";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(90);
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public EdmsCapstoneProcessTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [SqlServerFact]
    [Trait("Category", "EdmsCapstone")]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "ProcessRestart")]
    public async Task PendingDisciplineTasksSurviveAbruptProcessExit_AndFreshProcessCompletesReview()
    {
        var serverConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? throw new InvalidOperationException($"Set {ConnectionStringVariable} to run the SQL process-boundary capstone.");
        await using var database = await SqlServerTestDatabase.CreateAsync(serverConnectionString);
        using var workspace = new ProcessWorkspace();
        await using var processA = StartChild(workspace.Path, database.ConnectionString, "capstone-hold");

        var suspended = await processA.WaitForMessageAsync();
        var workflowInstanceId = GetString(suspended, "workflowInstanceId");
        Assert.Equal("Running", GetString(suspended, "status"));
        Assert.Equal("Suspended", GetString(suspended, "subStatus"));
        Assert.Equal(3, GetInt32(suspended, "bookmarkCount"));
        Assert.Equal(3, GetInt32(suspended, "taskCount"));
        _output.WriteLine($"Process A PID {processA.ProcessId} persisted three discipline tasks and Elsa bookmarks.");

        var taskStore = new SqlEdmsReviewTaskStore(database.ConnectionString);
        var persistedTasks = await taskStore.FindByWorkflowAsync(workflowInstanceId, CancellationToken.None);
        Assert.Equal(3, persistedTasks.Count);
        Assert.Equal(3, persistedTasks.Select(task => task.TaskId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, persistedTasks.Select(task => task.BookmarkId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(persistedTasks, task => Assert.Equal(ReviewTaskStatus.Assigned, task.Status));

        await using (var readHost = await ElsaSqlTestHost.CreateAsync(database.ConnectionString))
        {
            var stored = await readHost.Services.GetRequiredService<IWorkflowInstanceStore>()
                .FindAsync(new WorkflowInstanceFilter { Id = workflowInstanceId });
            Assert.NotNull(stored);
            Assert.Equal(WorkflowStatus.Running, stored!.Status);
            Assert.Equal(WorkflowSubStatus.Suspended, stored.SubStatus);
            Assert.Equal(3, stored.WorkflowState.Bookmarks.Count);
            Assert.Empty(stored.WorkflowState.Incidents);
        }

        await processA.KillTreeAndWaitAsync();
        Assert.True(processA.HasExited);
        _output.WriteLine($"Process A PID {processA.ProcessId} was terminated while still holding its suspended review host.");

        var processB = await RunChildAsync(workspace.Path, database.ConnectionString,
            "capstone-complete", workflowInstanceId);
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        Assert.Equal(workflowInstanceId, GetString(processB.Message, "workflowInstanceId"));
        Assert.Equal("Finished", GetString(processB.Message, "status"));
        Assert.Equal("Finished", GetString(processB.Message, "subStatus"));
        Assert.Equal(3, GetInt32(processB.Message, "completedTaskCount"));
        Assert.Equal(0, GetInt32(processB.Message, "bookmarksLeft"));
        Assert.Equal("RevisionRequired", GetString(processB.Message, "finalStatus"));
        Assert.Equal(1, GetInt32(processB.Message, "commentCount"));
        Assert.Equal(0, GetInt32(processB.Message, "incidentCount"));
        Assert.False(GetBoolean(processB.Message, "hasFaultedActivity"));
        _output.WriteLine($"Process B PID {processB.ProcessId} loaded the SQL state and completed all three tasks; the workflow finished with one EDMS comment.");

        persistedTasks = await taskStore.FindByWorkflowAsync(workflowInstanceId, CancellationToken.None);
        Assert.All(persistedTasks, task => Assert.Equal(ReviewTaskStatus.Completed, task.Status));
        Assert.Equal(3, persistedTasks.Select(task => task.BookmarkId).Distinct(StringComparer.Ordinal).Count());
    }

    private static async Task<ChildResult> RunChildAsync(string workingDirectory, string connectionString, params string[] args)
    {
        await using var child = StartChild(workingDirectory, connectionString, args);
        var message = await child.WaitForMessageAsync();
        var exitCode = await child.WaitForExitAsync();
        if (exitCode != 0)
            throw new Xunit.Sdk.XunitException($"Child exited with code {exitCode}.\n{child.Diagnostics}");
        return new(child.ProcessId, message);
    }

    private static CapstoneChild StartChild(string workingDirectory, string connectionString, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(FindHarnessAssembly());
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment[ConnectionStringVariable] = connectionString;
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the EDMS capstone process harness.");
        return new CapstoneChild(process);
    }

    private static string FindHarnessAssembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = directory.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine test build configuration.");
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
            directory = directory.Parent;
        if (directory == null)
            throw new DirectoryNotFoundException("Could not locate the ElsaLab repository root.");
        var assembly = Path.Combine(directory.FullName, "tests", "ElsaLab.ProcessHarness", "bin", configuration, "net10.0", "ElsaLab.ProcessHarness.dll");
        return File.Exists(assembly) ? assembly : throw new FileNotFoundException("The process harness assembly was not built.", assembly);
    }

    private static string GetString(JsonElement message, string property) => Property(message, property).GetString()!;
    private static int GetInt32(JsonElement message, string property) => Property(message, property).GetInt32();
    private static bool GetBoolean(JsonElement message, string property) => Property(message, property).GetBoolean();
    private static JsonElement Property(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private sealed record ChildResult(int ProcessId, JsonElement Message);

    private sealed class ProcessWorkspace : IDisposable
    {
        public ProcessWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"elsalab-edms-capstone-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class CapstoneChild : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly object _sync = new();
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly TaskCompletionSource<JsonElement> _message = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CapstoneChild(Process process)
        {
            _process = process;
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived += OnError;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;
        public string Diagnostics
        {
            get
            {
                lock (_sync)
                    return $"stdout:\n{_stdout}\nstderr:\n{_stderr}";
            }
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
                throw new Xunit.Sdk.XunitException($"Child emitted no capstone JSON within {ChildTimeout}.\n{Diagnostics}");
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
                try { _process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
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
            lock (_sync)
                _stdout.AppendLine(args.Data);
            if (!args.Data.StartsWith(MessagePrefix, StringComparison.Ordinal))
                return;
            try
            {
                _message.TrySetResult(JsonDocument.Parse(args.Data[MessagePrefix.Length..]).RootElement.Clone());
            }
            catch (Exception exception)
            {
                _message.TrySetException(new Xunit.Sdk.XunitException($"Child emitted invalid capstone JSON: {exception.Message}\n{Diagnostics}"));
            }
        }

        private void OnError(object sender, DataReceivedEventArgs args)
        {
            if (args.Data is null)
                return;
            lock (_sync)
                _stderr.AppendLine(args.Data);
        }
    }
}
