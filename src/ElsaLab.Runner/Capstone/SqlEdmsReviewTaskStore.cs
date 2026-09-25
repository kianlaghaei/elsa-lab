using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ElsaLab.Runner.Capstone;

/// <summary>
/// SQL-backed EDMS task adapter used by the capstone process-boundary test. These tables are
/// application-owned and deliberately separate from Elsa Management and Runtime persistence.
/// </summary>
public sealed class SqlEdmsReviewTaskStore(string connectionString) : IEdmsReviewTaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task EnsureSchemaAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'dbo.EdmsCapstoneReviewTasks', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.EdmsCapstoneReviewTasks
                (
                    TaskId nvarchar(450) NOT NULL CONSTRAINT PK_EdmsCapstoneReviewTasks PRIMARY KEY,
                    WorkflowInstanceId nvarchar(450) NOT NULL,
                    ReviewCycleId nvarchar(450) NOT NULL,
                    PayloadJson nvarchar(max) NOT NULL,
                    CreatedAt datetimeoffset NOT NULL CONSTRAINT DF_EdmsCapstoneReviewTasks_CreatedAt DEFAULT SYSUTCDATETIME()
                );
                CREATE INDEX IX_EdmsCapstoneReviewTasks_WorkflowInstanceId
                    ON dbo.EdmsCapstoneReviewTasks(WorkflowInstanceId);
                CREATE INDEX IX_EdmsCapstoneReviewTasks_ReviewCycleId
                    ON dbo.EdmsCapstoneReviewTasks(ReviewCycleId);
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EdmsReviewTask> CreateOrGetAsync(EdmsReviewTask task, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        string? existingJson;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT PayloadJson FROM dbo.EdmsCapstoneReviewTasks WITH (UPDLOCK, HOLDLOCK) WHERE TaskId = @taskId";
            select.Parameters.Add("@taskId", SqlDbType.NVarChar, 450).Value = task.TaskId;
            existingJson = await select.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (existingJson is not null)
        {
            var existing = Deserialize(existingJson);
            if (!SameRequest(existing, task))
                throw new InvalidOperationException($"Elsa task ID '{task.TaskId}' was reused for a different EDMS review assignment.");
            await transaction.CommitAsync(cancellationToken);
            return existing;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO dbo.EdmsCapstoneReviewTasks(TaskId, WorkflowInstanceId, ReviewCycleId, PayloadJson) VALUES (@taskId, @workflowId, @cycleId, @payload)";
            AddTaskParameters(insert, task);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return task;
    }

    public async Task<EdmsReviewTask?> FindAsync(string taskId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM dbo.EdmsCapstoneReviewTasks WHERE TaskId = @taskId";
        command.Parameters.Add("@taskId", SqlDbType.NVarChar, 450).Value = taskId;
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return json is null ? null : Deserialize(json);
    }

    public Task<IReadOnlyList<EdmsReviewTask>> FindByWorkflowAsync(string workflowInstanceId, CancellationToken cancellationToken) =>
        FindManyAsync("WorkflowInstanceId", workflowInstanceId, cancellationToken);

    public Task<IReadOnlyList<EdmsReviewTask>> FindByReviewCycleAsync(string reviewCycleId, CancellationToken cancellationToken) =>
        FindManyAsync("ReviewCycleId", reviewCycleId, cancellationToken);

    public async Task SaveAsync(EdmsReviewTask task, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dbo.EdmsCapstoneReviewTasks SET WorkflowInstanceId = @workflowId, ReviewCycleId = @cycleId, PayloadJson = @payload WHERE TaskId = @taskId";
        AddTaskParameters(command, task);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new KeyNotFoundException($"EDMS review task '{task.TaskId}' was not found.");
    }

    private async Task<IReadOnlyList<EdmsReviewTask>> FindManyAsync(string column, string value, CancellationToken cancellationToken)
    {
        if (column is not ("WorkflowInstanceId" or "ReviewCycleId"))
            throw new ArgumentOutOfRangeException(nameof(column));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT PayloadJson FROM dbo.EdmsCapstoneReviewTasks WHERE {column} = @value ORDER BY TaskId";
        command.Parameters.Add("@value", SqlDbType.NVarChar, 450).Value = value;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tasks = new List<EdmsReviewTask>();
        while (await reader.ReadAsync(cancellationToken))
            tasks.Add(Deserialize(reader.GetString(0)));
        return tasks;
    }

    private static void AddTaskParameters(SqlCommand command, EdmsReviewTask task)
    {
        command.Parameters.Add("@taskId", SqlDbType.NVarChar, 450).Value = task.TaskId;
        command.Parameters.Add("@workflowId", SqlDbType.NVarChar, 450).Value = task.WorkflowInstanceId;
        command.Parameters.Add("@cycleId", SqlDbType.NVarChar, 450).Value = task.ReviewCycleId;
        command.Parameters.Add("@payload", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(task, JsonOptions);
    }

    private static EdmsReviewTask Deserialize(string json) =>
        JsonSerializer.Deserialize<EdmsReviewTask>(json, JsonOptions)
        ?? throw new InvalidOperationException("The persisted EDMS review task payload was empty.");

    private static bool SameRequest(EdmsReviewTask first, EdmsReviewTask second) =>
        first.OperationId == second.OperationId &&
        first.WorkflowInstanceId == second.WorkflowInstanceId &&
        first.BookmarkId == second.BookmarkId &&
        first.ReviewAssignmentId == second.ReviewAssignmentId &&
        first.ReviewCycleId == second.ReviewCycleId &&
        first.Discipline == second.Discipline;
}
