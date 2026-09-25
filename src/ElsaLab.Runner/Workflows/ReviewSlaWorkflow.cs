using Elsa.Scheduling.Activities;
using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

/// <summary>
/// A linear durable SLA timer sequence: deadline, reminder, reminder interval, escalation.
/// </summary>
public sealed class ReviewSlaWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumber = builder.WithInput<string>("DocumentNumber");
        var reviewKey = builder.WithInput<string>("ReviewKey");
        var initialDelay = builder.WithInput<TimeSpan>("InitialDelay");
        var reminderDelay = builder.WithInput<TimeSpan>("ReminderDelay");

        // Delay suspends and later rehydrates the workflow. Copy caller inputs into workflow-owned
        // variables before the first timer so later activities do not depend on request-local input.
        var documentNumberValue = builder.WithVariable<string>("SlaDocumentNumber", string.Empty).WithWorkflowStorage();
        var reviewKeyValue = builder.WithVariable<string>("SlaReviewKey", string.Empty).WithWorkflowStorage();
        var initialDelayValue = builder.WithVariable<TimeSpan>("SlaInitialDelay", TimeSpan.Zero).WithWorkflowStorage();
        var reminderDelayValue = builder.WithVariable<TimeSpan>("SlaReminderDelay", TimeSpan.Zero).WithWorkflowStorage();

        builder.WithOutput<int>("ReminderCount");
        builder.WithOutput<int>("EscalationCount");
        builder.WithOutput<bool>("Escalated");
        builder.WithOutput<string>("FinalSlaStatus");
        builder.WithOutput<string>("FinalDocumentNumber");
        builder.WithOutput<string>("FinalReviewKey");

        var reminderCount = builder.WithVariable<int>("ReminderCount", 0).WithWorkflowStorage();
        var escalationCount = builder.WithVariable<int>("EscalationCount", 0).WithWorkflowStorage();
        var escalated = builder.WithVariable<bool>("Escalated", false).WithWorkflowStorage();
        var finalStatus = builder.WithVariable<string>("FinalSlaStatus", "Assigned").WithWorkflowStorage();

        Func<ExpressionExecutionContext, TimeSpan> getInitialDelay = context => initialDelayValue.Get(context)!;
        Func<ExpressionExecutionContext, TimeSpan> getReminderDelay = context => reminderDelayValue.Get(context)!;
        Func<ExpressionExecutionContext, string> getReviewKey = context => reviewKeyValue.Get(context)!;
        Func<ExpressionExecutionContext, string> getReminderOperationId = context => $"Reminder:{reviewKeyValue.Get(context)}:1";
        Func<ExpressionExecutionContext, string> getEscalationOperationId = context => $"Escalation:{reviewKeyValue.Get(context)}";

        var firstDelay = new Delay(getInitialDelay)
        {
            Name = "InitialSlaDelay"
        };
        var reminder = new SendReviewReminderActivity
        {
            Name = "SendReviewReminder",
            ReviewKey = new Input<string>(getReviewKey),
            ReminderNumber = new(1),
            OperationId = new Input<string>(getReminderOperationId)
        };
        var secondDelay = new Delay(getReminderDelay)
        {
            Name = "ReminderEscalationDelay"
        };
        var escalation = new EscalateReviewActivity
        {
            Name = "EscalateReview",
            ReviewKey = new Input<string>(getReviewKey),
            OperationId = new Input<string>(getEscalationOperationId)
        };

        builder.Root = new Sequence
        {
            Name = "ReviewSlaSequence",
            Activities =
            {
                new SetVariable<string>(documentNumberValue, context => context.GetInput<string>(documentNumber)!)
                {
                    Name = "CaptureDocumentNumber"
                },
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
                new WriteLine(context => $"SLA assigned for {documentNumberValue.Get(context)} ({reviewKeyValue.Get(context)})")
                {
                    Name = "AssignReview"
                },
                firstDelay,
                reminder,
                new SetVariable<int>(reminderCount, context => reminderCount.Get(context)! + 1)
                {
                    Name = "RecordReminder"
                },
                new SetVariable<string>(finalStatus, "ReminderSent")
                {
                    Name = "MarkReminderSent"
                },
                secondDelay,
                escalation,
                new SetVariable<int>(escalationCount, context => escalationCount.Get(context)! + 1)
                {
                    Name = "RecordEscalation"
                },
                new SetVariable<bool>(escalated, true)
                {
                    Name = "MarkEscalated"
                },
                new SetVariable<string>(finalStatus, "Escalated")
                {
                    Name = "MarkSlaEscalated"
                },
                new SetOutput
                {
                    OutputName = new("ReminderCount"),
                    OutputValue = new(context => reminderCount.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("EscalationCount"),
                    OutputValue = new(context => escalationCount.Get(context)!)
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
                new SetOutput
                {
                    OutputName = new("FinalDocumentNumber"),
                    OutputValue = new(context => documentNumberValue.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("FinalReviewKey"),
                    OutputValue = new(context => reviewKeyValue.Get(context)!)
                },
                new SetVariable<string>(Result, context =>
                    $"{documentNumberValue.Get(context)} SLA escalated after one reminder")
                {
                    Name = "CompleteSla"
                }
            }
        };
    }
}
