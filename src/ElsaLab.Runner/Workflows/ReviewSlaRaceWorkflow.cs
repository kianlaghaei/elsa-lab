using Elsa.Scheduling.Activities;
using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

/// <summary>
/// Races an external review-completion Event against native Delay bookmarks.
/// Elsa's Break cancels the remaining branch inside the While scope.
/// </summary>
public sealed class ReviewSlaRaceWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var reviewKey = builder.WithInput<string>("ReviewKey");
        var initialDelay = builder.WithInput<TimeSpan>("InitialDelay");
        var reminderDelay = builder.WithInput<TimeSpan>("ReminderDelay");

        var reviewKeyValue = builder.WithVariable<string>("SlaReviewKey", string.Empty).WithWorkflowStorage();
        var initialDelayValue = builder.WithVariable<TimeSpan>("SlaInitialDelay", TimeSpan.Zero).WithWorkflowStorage();
        var reminderDelayValue = builder.WithVariable<TimeSpan>("SlaReminderDelay", TimeSpan.Zero).WithWorkflowStorage();
        Func<ExpressionExecutionContext, TimeSpan> getInitialDelay = context => initialDelayValue.Get(context)!;
        Func<ExpressionExecutionContext, TimeSpan> getReminderDelay = context => reminderDelayValue.Get(context)!;
        Func<ExpressionExecutionContext, string> getReviewKey = context => reviewKeyValue.Get(context)!;
        Func<ExpressionExecutionContext, string> getReminderOperationId = context => $"Reminder:{reviewKeyValue.Get(context)}:1";
        Func<ExpressionExecutionContext, string> getEscalationOperationId = context => $"Escalation:{reviewKeyValue.Get(context)}";

        builder.WithOutput<int>("ReminderCount");
        builder.WithOutput<bool>("Escalated");
        builder.WithOutput<string>("FinalSlaStatus");

        var reminderCount = builder.WithVariable<int>("ReminderCount", 0).WithWorkflowStorage();
        var escalated = builder.WithVariable<bool>("Escalated", false).WithWorkflowStorage();
        var finalStatus = builder.WithVariable<string>("FinalSlaStatus", "Assigned").WithWorkflowStorage();

        var reviewCompletedBranch = new Sequence
        {
            Name = "ReviewCompletedBranch",
            Activities =
            {
                new Event("ReviewCompleted")
                {
                    Name = "WaitForReviewCompleted",
                    Id = "ReviewCompletedEvent"
                },
                new SetVariable<string>(finalStatus, "OnTime")
                {
                    Name = "MarkReviewCompletedOnTime"
                },
                new Break()
                {
                    Name = "CancelSlaAfterReviewCompletion"
                }
            }
        };

        var timerBranch = new Sequence
        {
            Name = "SlaTimerBranch",
            Activities =
            {
                new Delay(getInitialDelay)
                {
                    Name = "InitialSlaDelay"
                },
                new SendReviewReminderActivity
                {
                    Name = "SendReviewReminder",
                    ReviewKey = new Input<string>(getReviewKey),
                    ReminderNumber = new(1),
                    OperationId = new Input<string>(getReminderOperationId)
                },
                new SetVariable<int>(reminderCount, context => reminderCount.Get(context)! + 1)
                {
                    Name = "RecordReminder"
                },
                new SetVariable<string>(finalStatus, "ReminderSent")
                {
                    Name = "MarkReminderSent"
                },
                new Delay(getReminderDelay)
                {
                    Name = "ReminderEscalationDelay"
                },
                new EscalateReviewActivity
                {
                    Name = "EscalateReview",
                    ReviewKey = new Input<string>(getReviewKey),
                    OperationId = new Input<string>(getEscalationOperationId)
                },
                new SetVariable<bool>(escalated, true)
                {
                    Name = "MarkEscalated"
                },
                new SetVariable<string>(finalStatus, "Escalated")
                {
                    Name = "MarkSlaEscalated"
                },
                new Break()
                {
                    Name = "CancelReviewWaitAfterEscalation"
                }
            }
        };

        var raceScope = new While(() => true)
        {
            Name = "ReviewCompletionOrSlaRace",
            Body = new Fork
            {
                Name = "RaceReviewAgainstSla",
                JoinMode = ForkJoinMode.WaitAll,
                Branches = { reviewCompletedBranch, timerBranch }
            }
        };

        builder.Root = new Sequence
        {
            Name = "ReviewSlaRaceSequence",
            Activities =
            {
                new SetVariable<string>(reviewKeyValue, context => context.GetInput<string>(reviewKey)!)
                {
                    Name = "CaptureReviewKey"
                },
                new SetVariable<TimeSpan>(initialDelayValue, context => context.GetInput<TimeSpan>(initialDelay))
                {
                    Name = "CaptureInitialDelay"
                },
                new SetVariable<TimeSpan>(reminderDelayValue, context => context.GetInput<TimeSpan>(reminderDelay))
                {
                    Name = "CaptureReminderDelay"
                },
                raceScope,
                new SetOutput
                {
                    OutputName = new("ReminderCount"),
                    OutputValue = new(context => reminderCount.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("Escalated"),
                    OutputValue = new(context => escalated.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("FinalSlaStatus"),
                    OutputValue = new(context => finalStatus.Get(context)!)
                },
                new SetVariable<string>(Result, context =>
                    $"Review {context.GetInput<string>(reviewKey)} finished with SLA status {finalStatus.Get(context)}")
                {
                    Name = "CompleteReviewSla"
                }
            }
        };
    }
}
