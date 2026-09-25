using System.Diagnostics;
using System.Text.Json;
using ElsaLab.Runner.Services;

namespace ElsaLab.Tests;

public sealed class DocumentPublicationProcessRestartTests
{
    private const string SqlServerConnectionVariable = "ELSALAB_SQLSERVER_CONNECTION_STRING";
    private const string JsonMessagePrefix = "ELSA_PROCESS_RESULT=";
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(90);
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public DocumentPublicationProcessRestartTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [ProcessRestartFact]
    [Trait("Category", "ProcessRestart")]
    [Trait("Category", "SqlServer")]
    [Trait("Category", "Resilience")]
    public async Task FaultAndIncidentSurviveAnActualOsProcessExit()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(SqlServerConnectionVariable)
            ?? throw new InvalidOperationException($"Set {SqlServerConnectionVariable} to run SQL Server process tests.");
        await using var database = await SqlServerTestDatabase.CreateAsync(configuredConnectionString);
        using var workspace = new ProcessWorkspace();

        var processA = await RunChildAsync(workspace.Path, database.ConnectionString, "failure-fault");
        Assert.Equal("Finished", GetString(processA.Message, "status"));
        Assert.Equal("Faulted", GetString(processA.Message, "subStatus"));
        Assert.Equal("Faulted", GetString(processA.Message, "activityStatus"));
        Assert.False(GetBoolean(processA.Message, "downstreamExecuted"));
        Assert.Equal(1, GetInt32(processA.Message, "incidentCount"));
        Assert.Equal("The revision is not eligible for publication.", GetString(processA.Message, "incidentMessage"));
        Assert.Equal(typeof(DocumentValidationException).FullName, GetString(processA.Message, "incidentExceptionType"));
        _output.WriteLine($"Process A PID {processA.ProcessId} faulted the workflow and exited with its incident committed.");

        var workflowInstanceId = GetString(processA.Message, "workflowInstanceId");
        var processB = await RunChildAsync(workspace.Path, database.ConnectionString, "failure-inspect", workflowInstanceId);
        Assert.NotEqual(processA.ProcessId, processB.ProcessId);
        Assert.Equal(workflowInstanceId, GetString(processB.Message, "workflowInstanceId"));
        Assert.Equal("Finished", GetString(processB.Message, "status"));
        Assert.Equal("Faulted", GetString(processB.Message, "subStatus"));
        Assert.Equal(GetString(processA.Message, "activityId"), GetString(processB.Message, "activityId"));
        Assert.Equal(GetString(processA.Message, "incidentMessage"), GetString(processB.Message, "incidentMessage"));
        // The incident's serialized ExceptionState retains the message but deserializes its Type as System.Exception.
        Assert.Equal(typeof(Exception).FullName, GetString(processB.Message, "incidentExceptionType"));
        Assert.Equal(GetString(processA.Message, "incidentMessage"), GetString(processB.Message, "incidentExceptionMessage"));
        Assert.Equal(1, GetInt32(processB.Message, "incidentCount"));
        Assert.Equal(0, GetInt32(processB.Message, "bookmarkCount"));
        _output.WriteLine($"Process B PID {processB.ProcessId} loaded the same SQL fault incident after a distinct OS process started.");
    }

    private static async Task<ProcessResult> RunChildAsync(string workingDirectory, string connectionString, params string[] arguments)
    {
        var harness = FindHarnessAssembly();
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(harness);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment[SqlServerConnectionVariable] = connectionString;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ElsaLab.ProcessHarness.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ChildTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new Xunit.Sdk.XunitException(
                $"Child process timed out after {ChildTimeout}. stdout:\n{await stdout}\nstderr:\n{await stderr}");
        }

        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new Xunit.Sdk.XunitException($"Child exited {process.ExitCode}. stdout:\n{output}\nstderr:\n{error}");
        var jsonLine = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith(JsonMessagePrefix, StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"Child emitted no result JSON. stdout:\n{output}\nstderr:\n{error}");
        using var json = JsonDocument.Parse(jsonLine[JsonMessagePrefix.Length..]);
        return new(process.Id, json.RootElement.Clone());
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

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.EnumerateObject().Single(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
    private static string GetString(JsonElement element, string name) => GetProperty(element, name).GetString()!;
    private static int GetInt32(JsonElement element, string name) => GetProperty(element, name).GetInt32();
    private static bool GetBoolean(JsonElement element, string name) => GetProperty(element, name).GetBoolean();

    private sealed record ProcessResult(int ProcessId, JsonElement Message);

    private sealed class ProcessWorkspace : IDisposable
    {
        public ProcessWorkspace()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"elsalab-failure-process-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
