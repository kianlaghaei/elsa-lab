using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace ElsaLab.Tests;

public sealed class ElsaProcessRestartTests
{
    private const string SqlServerConnectionVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";
    private const string JsonMessagePrefix = "ELSA_PROCESS_RESULT=";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(75);
    private readonly ITestOutputHelper _output;

    public ElsaProcessRestartTests(ITestOutputHelper output) => _output = output;

    [ProcessRestartFact]
    [Trait("Category", "ProcessRestart")]
    public async Task GracefulExit_InspectionSurvivesAnotherProcess_AndResumesInThirdProcess()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        using var workspace = new ProcessWorkspace();

        var processA = await RunChildAsync(workspace, database.ConnectionString, "suspend");
        Assert.Equal(0, processA.ExitCode);
        Assert.Equal("Running", GetString(processA.Message, "status"));
        Assert.Equal("Suspended", GetString(processA.Message, "subStatus"));
        Assert.True(GetBoolean(processA.Message, "bookmarkExists"));
        Assert.Equal(0, GetInt32(processA.Message, "incidentCount"));
        _output.WriteLine($"Process A suspend PID: {processA.ProcessId}; OS process exited normally.");

        var workflowInstanceId = GetString(processA.Message, "workflowInstanceId");
        var bookmarkId = GetString(processA.Message, "bookmarkId");
        var processB = await RunChildAsync(workspace, database.ConnectionString, "inspect", workflowInstanceId, bookmarkId);
        AssertInspectionMatchesSuspended(processA.Message, processB.Message);
        Assert.Equal(0, GetInt32(processB.Message, "inputCount"));
        Assert.Equal(0, GetInt32(processB.Message, "outputCount"));
        _output.WriteLine($"Process B read-only inspect PID: {processB.ProcessId}.");

        var processC = await RunChildAsync(workspace, database.ConnectionString, "resume", workflowInstanceId, bookmarkId);
        AssertFinished(processC.Message, workflowInstanceId, bookmarkId, "DPC-10-ME-0001", 2, "discipline-review");
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        Assert.NotEqual(processB.ProcessId, processC.ProcessId);
        Assert.NotEqual(processA.ProcessId, processC.ProcessId);
        _output.WriteLine($"Process C resume PID: {processC.ProcessId}.");
    }

    [ProcessRestartFact]
    [Trait("Category", "ProcessRestart")]
    public async Task AbruptKill_AfterCommittedBookmark_RemainsResumableInFreshProcess()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        using var workspace = new ProcessWorkspace();
        await using var processA = StartChild(workspace, database.ConnectionString, "hold");

        var suspendedMessage = await processA.WaitForMessageAsync();
        Assert.Equal(processA.ProcessId, GetInt32(suspendedMessage, "processId"));
        Assert.Equal("Running", GetString(suspendedMessage, "status"));
        Assert.Equal("Suspended", GetString(suspendedMessage, "subStatus"));
        Assert.False(processA.HasExited);
        _output.WriteLine($"Process A suspended and waiting without shutdown, PID: {processA.ProcessId}.");

        var workflowInstanceId = GetString(suspendedMessage, "workflowInstanceId");
        var bookmarkId = GetString(suspendedMessage, "bookmarkId");
        var processB = await RunChildAsync(workspace, database.ConnectionString, "inspect", workflowInstanceId, bookmarkId);
        AssertInspectionMatchesSuspended(suspendedMessage, processB.Message);
        _output.WriteLine($"Process B confirmed SQL commit while Process A remained alive, PID: {processB.ProcessId}.");

        await processA.KillTreeAndWaitAsync();
        Assert.True(processA.HasExited);
        _output.WriteLine($"Process A was terminated by Process.Kill(entireProcessTree: true).");

        var processC = await RunChildAsync(workspace, database.ConnectionString, "resume", workflowInstanceId, bookmarkId);
        AssertFinished(processC.Message, workflowInstanceId, bookmarkId, "DPC-10-ME-0001", 2, "discipline-review");
        Assert.NotEqual(processA.ProcessId, processC.ProcessId);
        _output.WriteLine($"Process C resumed after abrupt Process A termination, PID: {processC.ProcessId}.");
    }

    [ProcessRestartFact]
    [Trait("Category", "ProcessRestart")]
    public async Task HostedStartupWithoutExplicitWorkflowRegistration_LoadsPersistedDefinitionAndResumes()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        using var workspace = new ProcessWorkspace();
        var processA = await RunChildAsync(workspace, database.ConnectionString, "suspend");
        var workflowInstanceId = GetString(processA.Message, "workflowInstanceId");
        var bookmarkId = GetString(processA.Message, "bookmarkId");

        var processB = await RunChildAsync(workspace, database.ConnectionString, "resume-unregistered", workflowInstanceId, bookmarkId);
        Assert.False(GetBoolean(processB.Message, "explicitWorkflowRegistration"));
        Assert.True(GetBoolean(processB.Message, "succeeded"));
        Assert.True(GetBoolean(processB.Message, "resumerReturnedResponse"));
        Assert.Equal("Finished", GetString(processB.Message, "status"));
        Assert.Equal("Finished", GetString(processB.Message, "subStatus"));
        Assert.False(GetBoolean(processB.Message, "bookmarkExists"));
        Assert.Equal(0, GetInt32(processB.Message, "incidentCount"));
        Assert.Equal("ReviewCompleted", GetString(GetProperty(processB.Message, "outputs"), "FinalStatus"));
        _output.WriteLine($"Process B resumed from the persisted typed workflow definition without an explicit IWorkflowRegistry.RegisterAsync call; PID: {processB.ProcessId}.");

        var processC = await RunChildAsync(workspace, database.ConnectionString, "resume", workflowInstanceId, bookmarkId);
        Assert.False(GetBoolean(processC.Message, "resumerReturnedResponse"));
        Assert.Equal("Finished", GetString(processC.Message, "status"));
        Assert.Equal("Finished", GetString(processC.Message, "subStatus"));
        Assert.False(GetBoolean(processC.Message, "bookmarkExists"));
        Assert.Equal("ReviewCompleted", GetString(GetProperty(processC.Message, "outputs"), "FinalStatus"));
        Assert.Equal(0, GetInt32(processC.Message, "incidentCount"));
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        Assert.NotEqual(processB.ProcessId, processC.ProcessId);
        _output.WriteLine($"Process C duplicate resume returned no response and left the completed persisted state unchanged; PID: {processC.ProcessId}.");
    }

    [ProcessRestartFact]
    [Trait("Category", "ProcessRestart")]
    public async Task TwoWorkflowInstances_RemainIndependentAcrossSeveralProcesses()
    {
        await using var database = await SqlServerTestDatabase.CreateAsync(GetConnectionString());
        using var workspace = new ProcessWorkspace();

        var processA = await RunChildAsync(workspace, database.ConnectionString, "suspend-two");
        var workflows = processA.Message.GetProperty("workflows").EnumerateArray().ToArray();
        Assert.Equal(2, workflows.Length);
        var a = workflows.Single(item => GetString(GetProperty(item, "payload"), "reviewKey") == "review-A");
        var b = workflows.Single(item => GetString(GetProperty(item, "payload"), "reviewKey") == "review-B");
        var workflowAId = GetString(a, "workflowInstanceId");
        var bookmarkAId = GetString(a, "bookmarkId");
        var workflowBId = GetString(b, "workflowInstanceId");
        var bookmarkBId = GetString(b, "bookmarkId");
        Assert.NotEqual(workflowAId, workflowBId);
        Assert.NotEqual(bookmarkAId, bookmarkBId);

        var processB = await RunChildAsync(workspace, database.ConnectionString,
            "resume-other", workflowAId, bookmarkAId, workflowBId, bookmarkBId);
        Assert.True(GetBoolean(processB.Message, "succeeded"));
        Assert.Equal("Finished", GetString(processB.Message, "status"));
        Assert.Equal("Finished", GetString(processB.Message, "subStatus"));
        Assert.False(GetBoolean(processB.Message, "bookmarkExists"));
        Assert.Equal("Running", GetString(processB.Message, "otherStatus"));
        Assert.Equal("Suspended", GetString(processB.Message, "otherSubStatus"));
        Assert.True(GetBoolean(processB.Message, "otherBookmarkExists"));
        Assert.True(GetBoolean(processB.Message, "otherOutputsEmpty"));
        Assert.Equal("DPC-10-ME-0001", GetString(GetProperty(processB.Message, "outputs"), "FinalDocumentNumber"));
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        _output.WriteLine($"Process A created two waits (PID {processA.ProcessId}); Process B resumed only A (PID {processB.ProcessId}).");

        var processC = await RunChildAsync(workspace, database.ConnectionString, "resume", workflowBId, bookmarkBId);
        AssertFinished(processC.Message, workflowBId, bookmarkBId, "DPC-10-ME-0002", 5, "review-B");
        Assert.NotEqual(processB.ProcessId, processC.ProcessId);
        _output.WriteLine($"Process C independently resumed B (PID {processC.ProcessId}).");
    }

    private static async Task<ProcessResult> RunChildAsync(ProcessWorkspace workspace, string connectionString, params string[] arguments)
    {
        await using var child = StartChild(workspace, connectionString, arguments);
        var message = await child.WaitForMessageAsync();
        var exitCode = await child.WaitForExitAsync();
        if (exitCode != 0)
            throw new Xunit.Sdk.XunitException($"Child process exited with {exitCode}.\n{child.Diagnostics}");
        return new(child.ProcessId, exitCode, message);
    }

    private static HarnessProcess StartChild(ProcessWorkspace workspace, string connectionString, params string[] arguments)
    {
        var harnessAssembly = FindHarnessAssembly();
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

    private static void AssertInspectionMatchesSuspended(JsonElement suspended, JsonElement inspected)
    {
        Assert.Equal("Running", GetString(inspected, "status"));
        Assert.Equal("Suspended", GetString(inspected, "subStatus"));
        Assert.True(GetBoolean(inspected, "bookmarkExists"));
        Assert.Equal(GetString(suspended, "workflowInstanceId"), GetString(inspected, "workflowInstanceId"));
        Assert.Equal(GetString(suspended, "bookmarkId"), GetString(inspected, "bookmarkId"));
        Assert.Equal(GetString(suspended, "bookmarkName"), GetString(inspected, "bookmarkName"));
        Assert.Equal(GetString(suspended, "bookmarkHash"), GetString(inspected, "bookmarkHash"));
        Assert.Equal(GetString(suspended, "activityInstanceId"), GetString(inspected, "activityInstanceId"));
        Assert.Equal(GetString(suspended, "workflowInstanceId"), GetString(inspected, "bookmarkWorkflowInstanceId"));
        Assert.Equal(GetString(GetProperty(suspended, "payload"), "documentNumber"), GetString(GetProperty(inspected, "payload"), "documentNumber"));
        Assert.Equal(GetInt32(GetProperty(suspended, "payload"), "revision"), GetInt32(GetProperty(inspected, "payload"), "revision"));
        Assert.Equal(GetString(GetProperty(suspended, "payload"), "reviewKey"), GetString(GetProperty(inspected, "payload"), "reviewKey"));
        Assert.Equal(0, GetInt32(inspected, "incidentCount"));
    }

    private static void AssertFinished(JsonElement message, string workflowInstanceId, string bookmarkId, string documentNumber, int revision, string reviewKey)
    {
        Assert.True(GetBoolean(message, "succeeded"));
        Assert.Equal(workflowInstanceId, GetString(message, "workflowInstanceId"));
        Assert.Equal(workflowInstanceId, GetString(message, "responseWorkflowInstanceId"));
        Assert.Equal("Finished", GetString(message, "status"));
        Assert.Equal("Finished", GetString(message, "subStatus"));
        Assert.Equal("Finished", GetString(message, "responseStatus"));
        Assert.Equal("Finished", GetString(message, "responseSubStatus"));
        Assert.False(GetBoolean(message, "bookmarkExists"));
        Assert.Equal(0, GetInt32(message, "incidentCount"));
        Assert.Equal(0, GetInt32(message, "responseIncidentCount"));

        var outputs = GetProperty(message, "outputs");
        Assert.Equal("ReviewCompleted", GetString(outputs, "FinalStatus"));
        Assert.True(GetBoolean(outputs, "Finalized"));
        Assert.Equal(documentNumber, GetString(outputs, "FinalDocumentNumber"));
        Assert.Equal(revision, GetInt32(outputs, "FinalRevision"));
        Assert.Equal(reviewKey, GetString(outputs, "FinalReviewKey"));
    }

    private static string GetConnectionString() =>
        Environment.GetEnvironmentVariable(SqlServerConnectionVariable)
        ?? throw new InvalidOperationException($"{SqlServerConnectionVariable} is required for process restart integration tests.");

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string GetString(JsonElement element, string name) => GetProperty(element, name).GetString()!;
    private static bool GetBoolean(JsonElement element, string name) => GetProperty(element, name).GetBoolean();
    private static int GetInt32(JsonElement element, string name) => GetProperty(element, name).GetInt32();

    private sealed record ProcessResult(int ProcessId, int ExitCode, JsonElement Message);

    private sealed class ProcessWorkspace : IDisposable
    {
        public ProcessWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"elsalab-process-restart-{Guid.NewGuid():N}");
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
            var session = new HarnessProcess(process);
            process.OutputDataReceived += session.OnOutput;
            process.ErrorDataReceived += session.OnError;
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Failed to start the ElsaLab process harness.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return session;
            }
            catch
            {
                process.Dispose();
                throw;
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
                throw new Xunit.Sdk.XunitException($"Child process did not emit its JSON result within {ChildTimeout}.\n{Diagnostics}");
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
                throw new Xunit.Sdk.XunitException($"Child process did not exit within {ChildTimeout}.\n{Diagnostics}");
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
                    // Process exited between the check and Kill.
                }
            }

            try
            {
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                throw new Xunit.Sdk.XunitException($"Killed child process did not terminate within 15 seconds.\n{Diagnostics}");
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                await KillTreeAndWaitAsync();
            _process.Dispose();
        }

        private void OnOutput(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
                return;

            lock (_gate)
                _stdout.AppendLine(args.Data);

            if (!args.Data.StartsWith(JsonMessagePrefix, StringComparison.Ordinal))
                return;

            try
            {
                var parsed = JsonDocument.Parse(args.Data[JsonMessagePrefix.Length..]).RootElement.Clone();
                _message.TrySetResult(parsed);
            }
            catch (Exception exception)
            {
                _message.TrySetException(new Xunit.Sdk.XunitException($"Child emitted malformed JSON: {exception.Message}\n{Diagnostics}"));
            }
        }

        private void OnError(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null)
                return;
            lock (_gate)
                _stderr.AppendLine(args.Data);
        }
    }
}

internal sealed class ProcessRestartFactAttribute : FactAttribute
{
    public ProcessRestartFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ELSALAB_SQLSERVER_CONNECTION_STRING")))
            Skip = "Set ELSALAB_SQLSERVER_CONNECTION_STRING to run process-restart SQL integration tests.";
    }
}
