using System.Collections.Concurrent;

namespace ElsaLab.Runner.Capstone;

public interface IEdmsReviewTaskStore
{
    Task<EdmsReviewTask> CreateOrGetAsync(EdmsReviewTask task, CancellationToken cancellationToken);
    Task<EdmsReviewTask?> FindAsync(string taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EdmsReviewTask>> FindByWorkflowAsync(string workflowInstanceId, CancellationToken cancellationToken);
    Task<IReadOnlyList<EdmsReviewTask>> FindByReviewCycleAsync(string reviewCycleId, CancellationToken cancellationToken);
    Task SaveAsync(EdmsReviewTask task, CancellationToken cancellationToken);
}

/// <summary>Test adapter for capstone scenarios that do not cross a process boundary.</summary>
public sealed class InMemoryEdmsReviewTaskStore : IEdmsReviewTaskStore
{
    private readonly ConcurrentDictionary<string, EdmsReviewTask> _tasks = new(StringComparer.Ordinal);

    public Task<EdmsReviewTask> CreateOrGetAsync(EdmsReviewTask task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = _tasks.GetOrAdd(task.TaskId, task);
        if (existing != task && !SameRequest(existing, task))
            throw new InvalidOperationException($"Elsa task ID '{task.TaskId}' was reused for a different EDMS review assignment.");
        return Task.FromResult(existing);
    }

    public Task<EdmsReviewTask?> FindAsync(string taskId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    public Task<IReadOnlyList<EdmsReviewTask>> FindByWorkflowAsync(string workflowInstanceId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EdmsReviewTask> tasks = _tasks.Values.Where(task => task.WorkflowInstanceId == workflowInstanceId)
            .OrderBy(task => task.Discipline, StringComparer.Ordinal).ToArray();
        return Task.FromResult(tasks);
    }

    public Task<IReadOnlyList<EdmsReviewTask>> FindByReviewCycleAsync(string reviewCycleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EdmsReviewTask> tasks = _tasks.Values.Where(task => task.ReviewCycleId == reviewCycleId)
            .OrderBy(task => task.Discipline, StringComparer.Ordinal).ToArray();
        return Task.FromResult(tasks);
    }

    public Task SaveAsync(EdmsReviewTask task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tasks.AddOrUpdate(task.TaskId,
            _ => throw new KeyNotFoundException($"EDMS review task '{task.TaskId}' was not found."),
            (_, _) => task);
        return Task.CompletedTask;
    }

    private static bool SameRequest(EdmsReviewTask first, EdmsReviewTask second) =>
        first.OperationId == second.OperationId &&
        first.WorkflowInstanceId == second.WorkflowInstanceId &&
        first.BookmarkId == second.BookmarkId &&
        first.ReviewAssignmentId == second.ReviewAssignmentId &&
        first.ReviewCycleId == second.ReviewCycleId &&
        first.Discipline == second.Discipline;
}

