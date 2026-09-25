using System.Collections.Concurrent;

namespace ElsaLab.Runner.Services;

public enum ReviewSlaActionKind
{
    Reminder,
    Escalation
}

public enum ReviewSlaOperationStatus
{
    Applied,
    AlreadyApplied
}

public sealed record ReviewSlaActionCommand(
    string OperationId,
    string ReviewKey,
    ReviewSlaActionKind Kind,
    int ReminderNumber);

public sealed record ReviewSlaActionResult(
    string OperationId,
    ReviewSlaOperationStatus Status,
    ReviewSlaActionCommand Command);

public interface IReviewSlaActionService
{
    Task<ReviewSlaActionResult> SendReminderAsync(
        ReviewSlaActionCommand command,
        CancellationToken cancellationToken);

    Task<ReviewSlaActionResult> EscalateAsync(
        ReviewSlaActionCommand command,
        CancellationToken cancellationToken);

    IReadOnlyCollection<ReviewSlaActionCommand> Actions { get; }
}

/// <summary>
/// Small in-process adapter for the experiment. Its operation key check demonstrates
/// application-owned idempotency; durable production de-duplication needs durable state.
/// </summary>
public sealed class InMemoryReviewSlaActionService : IReviewSlaActionService
{
    private readonly ConcurrentDictionary<string, ReviewSlaActionCommand> _actions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ReviewSlaActionCommand> _executionOrder = new();

    public IReadOnlyCollection<ReviewSlaActionCommand> Actions => _executionOrder.ToArray();

    public Task<ReviewSlaActionResult> SendReminderAsync(
        ReviewSlaActionCommand command,
        CancellationToken cancellationToken) => ApplyAsync(command, ReviewSlaActionKind.Reminder, cancellationToken);

    public Task<ReviewSlaActionResult> EscalateAsync(
        ReviewSlaActionCommand command,
        CancellationToken cancellationToken) => ApplyAsync(command, ReviewSlaActionKind.Escalation, cancellationToken);

    private Task<ReviewSlaActionResult> ApplyAsync(
        ReviewSlaActionCommand command,
        ReviewSlaActionKind expectedKind,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(command.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.ReviewKey);

        if (command.Kind != expectedKind)
            throw new ArgumentException($"Expected action kind {expectedKind}.", nameof(command));

        if (_actions.TryAdd(command.OperationId, command))
        {
            _executionOrder.Enqueue(command);
            return Task.FromResult(new ReviewSlaActionResult(command.OperationId, ReviewSlaOperationStatus.Applied, command));
        }

        var existing = _actions[command.OperationId];
        if (existing != command)
            throw new InvalidOperationException($"Operation ID '{command.OperationId}' was reused for a different SLA action.");

        return Task.FromResult(new ReviewSlaActionResult(command.OperationId, ReviewSlaOperationStatus.AlreadyApplied, existing));
    }
}
