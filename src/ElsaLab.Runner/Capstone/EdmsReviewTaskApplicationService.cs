using System.Globalization;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Activities;
using Elsa.Workflows.Runtime.Notifications;

namespace ElsaLab.Runner.Capstone;

public interface IEdmsReviewTaskApplicationService
{
    Task CreateFromElsaRequestAsync(RunTaskRequest request, CancellationToken cancellationToken);
    Task<EdmsReviewTask> ClaimAsync(string taskId, string userId, CancellationToken cancellationToken);
    Task<EdmsReviewTask> UnclaimAsync(string taskId, string userId, CancellationToken cancellationToken);
    Task<EdmsReviewTask> CompleteAsync(
        string taskId,
        string userId,
        ReviewDecision decision,
        string? commentText,
        CancellationToken cancellationToken);
    Task CancelWorkflowTasksAsync(string workflowInstanceId, CancellationToken cancellationToken);
}

/// <summary>
/// EDMS owns task assignment, claim, authorization checks, and result persistence.
/// Elsa owns the bookmark continuation. The Elsa bookmark ID is a locator, never an authorization token.
/// </summary>
public sealed class EdmsReviewTaskApplicationService(
    IEdmsReviewTaskStore store,
    IWorkflowResumer workflowResumer) : IEdmsReviewTaskApplicationService
{
    public async Task CreateFromElsaRequestAsync(RunTaskRequest request, CancellationToken cancellationToken)
    {
        var payload = request.TaskPayload ?? throw new InvalidOperationException("Review task payload is required.");
        var activityContext = request.ActivityExecutionContext;
        var bookmark = activityContext.Bookmarks.SingleOrDefault()
            ?? throw new InvalidOperationException("Elsa RunTask did not expose its created bookmark to the task dispatcher.");

        var task = new EdmsReviewTask(
            request.TaskId,
            Required(payload, "OperationId"),
            activityContext.WorkflowExecutionContext.Id,
            bookmark.Id,
            activityContext.Activity.Id,
            activityContext.Id,
            Required(payload, "CorrelationId"),
            Required(payload, "ProjectId"),
            Required(payload, "DocumentId"),
            Required(payload, "DocumentRevisionId"),
            Required(payload, "ReviewCycleId"),
            Required(payload, "ReviewAssignmentId"),
            Required(payload, "DocumentNumber"),
            Integer(payload, "Revision"),
            Required(payload, "Discipline"),
            Strings(payload, "CandidateUsers"),
            ReviewTaskStatus.Assigned,
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            null,
            Required(payload, "DefinitionVersionId"));

        await store.CreateOrGetAsync(task, cancellationToken);
    }

    public async Task<EdmsReviewTask> ClaimAsync(string taskId, string userId, CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(taskId, cancellationToken);
        if (task.Status == ReviewTaskStatus.Claimed && task.ClaimedBy == userId)
            return task;
        if (task.Status != ReviewTaskStatus.Assigned)
            throw new InvalidOperationException($"Task '{taskId}' is {task.Status} and cannot be claimed.");
        if (!task.CandidateUsers.Contains(userId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException($"User '{userId}' is not a candidate for task '{taskId}'.");

        var claimed = task with { Status = ReviewTaskStatus.Claimed, ClaimedBy = userId, ClaimedAt = DateTimeOffset.UtcNow };
        await store.SaveAsync(claimed, cancellationToken);
        return claimed;
    }

    public async Task<EdmsReviewTask> UnclaimAsync(string taskId, string userId, CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(taskId, cancellationToken);
        if (task.Status != ReviewTaskStatus.Claimed || task.ClaimedBy != userId)
            throw new UnauthorizedAccessException($"User '{userId}' does not own claimed task '{taskId}'.");
        var unclaimed = task with { Status = ReviewTaskStatus.Assigned, ClaimedBy = null, ClaimedAt = null };
        await store.SaveAsync(unclaimed, cancellationToken);
        return unclaimed;
    }

    public async Task<EdmsReviewTask> CompleteAsync(
        string taskId,
        string userId,
        ReviewDecision decision,
        string? commentText,
        CancellationToken cancellationToken)
    {
        var task = await GetTaskAsync(taskId, cancellationToken);
        if (task.Status == ReviewTaskStatus.Completed)
        {
            if (task.ClaimedBy == userId && task.Decision == decision && task.CommentText == commentText)
                return task;
            throw new InvalidOperationException($"Task '{taskId}' was already completed with a different command.");
        }
        if (task.Status != ReviewTaskStatus.Claimed || task.ClaimedBy != userId)
            throw new UnauthorizedAccessException($"User '{userId}' does not own task '{taskId}'.");
        if (decision == ReviewDecision.Commented && string.IsNullOrWhiteSpace(commentText))
            throw new ArgumentException("A comment is required for a Commented review.", nameof(commentText));

        var completed = task with
        {
            Status = ReviewTaskStatus.Completed,
            Decision = decision,
            CommentText = decision == ReviewDecision.Commented ? commentText : null,
            CompletedBy = userId,
            CompletedAt = DateTimeOffset.UtcNow
        };
        await store.SaveAsync(completed, cancellationToken);

        // Persist the authorized business result first, then use Elsa's exact bookmark target.
        // A repeated task command is resolved above without resuming a consumed bookmark again.
        var response = await workflowResumer.ResumeAsync(
            task.BookmarkId,
            new Dictionary<string, object> { [RunTask.InputKey] = decision.ToString() },
            cancellationToken);
        if (response is null)
            throw new InvalidOperationException($"Elsa bookmark '{task.BookmarkId}' was not found for task '{taskId}'.");

        return completed;
    }

    public async Task CancelWorkflowTasksAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        var tasks = await store.FindByWorkflowAsync(workflowInstanceId, cancellationToken);
        foreach (var task in tasks.Where(task => task.Status is ReviewTaskStatus.Assigned or ReviewTaskStatus.Claimed))
            await store.SaveAsync(task with { Status = ReviewTaskStatus.Cancelled }, cancellationToken);
    }

    private async Task<EdmsReviewTask> GetTaskAsync(string taskId, CancellationToken cancellationToken) =>
        await store.FindAsync(taskId, cancellationToken)
        ?? throw new KeyNotFoundException($"EDMS review task '{taskId}' was not found.");

    private static string Required(IDictionary<string, object> values, string key) =>
        Convert.ToString(values.TryGetValue(key, out var value) ? value : null, CultureInfo.InvariantCulture)
        ?? throw new InvalidOperationException($"The Elsa task request is missing '{key}'.");

    private static int Integer(IDictionary<string, object> values, string key) =>
        Convert.ToInt32(values.TryGetValue(key, out var value) ? value : null, CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> Strings(IDictionary<string, object> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
            return Array.Empty<string>();
        return value switch
        {
            string[] strings => strings,
            IReadOnlyList<string> strings => strings,
            IEnumerable<string> strings => strings.ToArray(),
            _ => throw new InvalidOperationException($"The task payload field '{key}' is not a string sequence.")
        };
    }
}

public sealed class EdmsReviewTaskDispatcher(IEdmsReviewTaskApplicationService taskService) : ITaskDispatcher
{
    public Task DispatchAsync(RunTaskRequest request, CancellationToken cancellationToken = default) =>
        taskService.CreateFromElsaRequestAsync(request, cancellationToken);
}

