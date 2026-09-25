using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class SendReviewReminderActivity : CodeActivity
{
    [Input]
    public Input<string> ReviewKey { get; set; } = null!;

    [Input]
    public Input<int> ReminderNumber { get; set; } = null!;

    [Input]
    public Input<string> OperationId { get; set; } = null!;

    [Output]
    public Output<string> OperationStatus { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var command = new ReviewSlaActionCommand(
            OperationId.Get(context),
            ReviewKey.Get(context),
            ReviewSlaActionKind.Reminder,
            ReminderNumber.Get(context));
        var result = await context.GetRequiredService<IReviewSlaActionService>()
            .SendReminderAsync(command, context.CancellationToken);
        OperationStatus.Set(context, result.Status.ToString());
    }
}

public sealed class EscalateReviewActivity : CodeActivity
{
    [Input]
    public Input<string> ReviewKey { get; set; } = null!;

    [Input]
    public Input<string> OperationId { get; set; } = null!;

    [Output]
    public Output<string> OperationStatus { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var command = new ReviewSlaActionCommand(
            OperationId.Get(context),
            ReviewKey.Get(context),
            ReviewSlaActionKind.Escalation,
            0);
        var result = await context.GetRequiredService<IReviewSlaActionService>()
            .EscalateAsync(command, context.CancellationToken);
        OperationStatus.Set(context, result.Status.ToString());
    }
}
