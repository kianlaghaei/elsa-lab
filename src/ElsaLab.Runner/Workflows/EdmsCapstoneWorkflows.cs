using Elsa.Extensions;
using Elsa.Expressions.Models;
using Elsa.Resilience.Models;
using Elsa.Scheduling.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Capstone;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Workflows;

public static class EdmsCapstoneDefinitionIdentity
{
    public const string DefinitionId = "EngineeringReviewCapstone";
    public const string Version1Id = "EngineeringReviewCapstone-v1";
    public const string Version2Id = "EngineeringReviewCapstone-v2";
}

public sealed class EdmsEngineeringReviewV1Workflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder) =>
        EdmsEngineeringReviewWorkflowBuilder.Build(builder, EdmsCapstoneDefinitionIdentity.Version1Id, 1, "V1", includeCoordinatorCheck: false);
}

public sealed class EdmsEngineeringReviewV2Workflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder) =>
        EdmsEngineeringReviewWorkflowBuilder.Build(builder, EdmsCapstoneDefinitionIdentity.Version2Id, 2, "V2", includeCoordinatorCheck: true);
}

internal static class EdmsEngineeringReviewWorkflowBuilder
{
    public static void Build(IWorkflowBuilder builder, string versionId, int version, string marker, bool includeCoordinatorCheck)
    {
        builder.WithDefinitionId(EdmsCapstoneDefinitionIdentity.DefinitionId);
        builder.WithId(versionId);
        builder.Version = version;
        builder.Name = $"EDMS Engineering Review {marker}";

        var projectId = builder.WithInput<string>("ProjectId");
        var correlationId = builder.WithInput<string>("CorrelationId");
        var documentId = builder.WithInput<string>("DocumentId");
        var documentRevisionId = builder.WithInput<string>("DocumentRevisionId");
        var documentNumber = builder.WithInput<string>("DocumentNumber");
        var revision = builder.WithInput<int>("Revision");
        var reviewCycleId = builder.WithInput<string>("ReviewCycleId");
        var definitionVersionId = builder.WithInput<string>("DefinitionVersionId");

        builder.WithOutput<string>("CorrelationId");
        builder.WithOutput<string>("DocumentId");
        builder.WithOutput<string>("DocumentRevisionId");
        builder.WithOutput<string>("LatestReceivedRevisionId");
        builder.WithOutput<string>("CurrentValidRevisionId");
        builder.WithOutput<int>("ReviewTaskCount");
        builder.WithOutput<int>("DistributionCount");
        builder.WithOutput<int>("CommentCount");
        builder.WithOutput<string>("FinalStatus");
        builder.WithOutput<string>("DefinitionMarker");
        builder.WithOutput<bool>("CoordinatorCheckExecuted");
        builder.WithOutput<string>("TransmittalNumber");
        builder.WithOutput<bool>("Finalized");

        var register = new RegisterCapstoneRevisionActivity
        {
            Name = "RegisterRevision",
            ProjectId = new(context => context.GetInput<string>(projectId)!),
            DocumentId = new(context => context.GetInput<string>(documentId)!),
            DocumentRevisionId = new(context => context.GetInput<string>(documentRevisionId)!),
            DocumentNumber = new(context => context.GetInput<string>(documentNumber)!),
            Revision = new(context => context.GetInput<int>(revision)),
            ReviewCycleId = new(context => context.GetInput<string>(reviewCycleId)!)
        };

        var distribute = new DistributeCapstoneRevisionActivity
        {
            Name = "DistributeRevision",
            DocumentRevisionId = new(context => context.GetInput<string>(documentRevisionId)!),
            Mode = new(DistributionMode.Reference)
        };

        var fork = new FlowFork
        {
            Name = "ForkDisciplineReviews",
            Branches = new(["Process", "Mechanical", "Instrument"])
        };

        var processTask = CreateReviewTask("Process", projectId, correlationId, documentId, documentRevisionId,
            documentNumber, revision, reviewCycleId, definitionVersionId);
        var mechanicalTask = CreateReviewTask("Mechanical", projectId, correlationId, documentId, documentRevisionId,
            documentNumber, revision, reviewCycleId, definitionVersionId);
        var instrumentTask = CreateReviewTask("Instrument", projectId, correlationId, documentId, documentRevisionId,
            documentNumber, revision, reviewCycleId, definitionVersionId);

        var join = new FlowJoin { Name = "WaitAllDisciplineReviews", Mode = new(FlowJoinMode.WaitAll) };
        var coordinatorCheck = new EdmsCoordinatorCheckActivity { Name = "CoordinatorCheck" };
        var consolidate = new ConsolidateCapstoneReviewsActivity
        {
            Name = "ConsolidateReviews"
        };
        var markRevisionRequired = new MarkCapstoneRevisionRequiredActivity { Name = "MarkRevisionRequired" };
        var approvedPath = new ApproveCapstoneRevisionActivity
        {
            Name = "ApprovePublishAndIssueTransmittal",
            CustomProperties =
            {
                ["resilienceStrategy"] = new ResilienceStrategyConfig
                {
                    Mode = ResilienceStrategyConfigMode.Identifier,
                    StrategyId = "document-publication"
                }
            }
        };
        var complete = new CompleteCapstoneWorkflowActivity
        {
            Name = "CompleteReviewProcess",
            DefinitionMarker = new(marker)
        };

        var activities = new List<IActivity>
        {
            register, distribute, fork, processTask, mechanicalTask, instrumentTask, join,
            consolidate, markRevisionRequired, approvedPath, complete
        };
        if (includeCoordinatorCheck)
            activities.Add(coordinatorCheck);

        var connections = new List<Connection>
        {
            Connect(register, distribute),
            Connect(distribute, fork),
            Connect(fork, processTask, "Process"),
            Connect(fork, mechanicalTask, "Mechanical"),
            Connect(fork, instrumentTask, "Instrument"),
            Connect(processTask, join),
            Connect(mechanicalTask, join),
            Connect(instrumentTask, join)
        };

        if (includeCoordinatorCheck)
        {
            connections.Add(Connect(join, coordinatorCheck));
            connections.Add(Connect(coordinatorCheck, consolidate));
        }
        else
            connections.Add(Connect(join, consolidate));

        connections.Add(Connect(consolidate, markRevisionRequired, "CommentsFound"));
        connections.Add(Connect(consolidate, approvedPath, "NoComments"));
        connections.Add(Connect(markRevisionRequired, complete));
        connections.Add(Connect(approvedPath, complete));

        builder.Root = new Flowchart
        {
            Name = $"EngineeringReview{marker}Flowchart",
            Start = register,
            Activities = activities,
            Connections = connections
        };
    }

    private static RunTask CreateReviewTask(
        string discipline,
        InputDefinition projectId,
        InputDefinition correlationId,
        InputDefinition documentId,
        InputDefinition documentRevisionId,
        InputDefinition documentNumber,
        InputDefinition revision,
        InputDefinition reviewCycleId,
        InputDefinition definitionVersionId)
    {
        var assignmentId = new Func<ExpressionExecutionContext, string>(
            context => $"{context.GetInput<string>(reviewCycleId)}:{discipline}");
        var operationId = new Func<ExpressionExecutionContext, string>(
            context => $"AssignReview:{context.GetInput<string>(reviewCycleId)}:{discipline}");
        var payload = new Func<ExpressionExecutionContext, IDictionary<string, object>?>(context => new Dictionary<string, object>
        {
            ["OperationId"] = operationId(context),
            ["CorrelationId"] = context.GetInput<string>(correlationId)!,
            ["ProjectId"] = context.GetInput<string>(projectId)!,
            ["DocumentId"] = context.GetInput<string>(documentId)!,
            ["DocumentRevisionId"] = context.GetInput<string>(documentRevisionId)!,
            ["ReviewCycleId"] = context.GetInput<string>(reviewCycleId)!,
            ["ReviewAssignmentId"] = assignmentId(context),
            ["DocumentNumber"] = context.GetInput<string>(documentNumber)!,
            ["Revision"] = context.GetInput<int>(revision),
            ["Discipline"] = discipline,
            ["CandidateUsers"] = new[] { $"{discipline.ToLowerInvariant()}-reviewer" },
            ["DefinitionVersionId"] = context.GetInput<string>(definitionVersionId)!
        });

        return new RunTask($"DisciplineReview:{discipline}")
        {
            Name = $"Review{discipline}",
            Payload = new(payload)
        };
    }

    private static Connection Connect(IActivity source, IActivity target, string outcome = "Done") =>
        new(source, target) { Source = new(source, outcome) };

}

/// <summary>One task competes with native Delay bookmarks; completion breaks the timer branch.</summary>
public sealed class EdmsReviewSlaRaceWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var projectId = builder.WithInput<string>("ProjectId");
        var correlationId = builder.WithInput<string>("CorrelationId");
        var documentId = builder.WithInput<string>("DocumentId");
        var documentRevisionId = builder.WithInput<string>("DocumentRevisionId");
        var documentNumber = builder.WithInput<string>("DocumentNumber");
        var revision = builder.WithInput<int>("Revision");
        var reviewCycleId = builder.WithInput<string>("ReviewCycleId");
        var definitionVersionId = builder.WithInput<string>("DefinitionVersionId");
        var initialDelay = builder.WithInput<TimeSpan>("InitialDelay");
        var reminderDelay = builder.WithInput<TimeSpan>("ReminderDelay");
        var reviewKey = builder.WithInput<string>("ReviewKey");

        builder.WithOutput<string>("FinalSlaStatus");
        var status = builder.WithVariable<string>("CapstoneSlaStatus", "Pending").WithWorkflowStorage();
        Func<ExpressionExecutionContext, string> getStatus = context => status.Get(context)!;
        Func<ExpressionExecutionContext, string> getReviewKey = context => context.GetInput<string>(reviewKey)!;

        var assignmentId = new Func<ExpressionExecutionContext, string>(context => $"{context.GetInput<string>(reviewCycleId)}:Mechanical");
        var payload = new Func<ExpressionExecutionContext, IDictionary<string, object>?>(context => new Dictionary<string, object>
        {
            ["OperationId"] = $"AssignReview:{context.GetInput<string>(reviewCycleId)}:Mechanical",
            ["CorrelationId"] = context.GetInput<string>(correlationId)!,
            ["ProjectId"] = context.GetInput<string>(projectId)!,
            ["DocumentId"] = context.GetInput<string>(documentId)!,
            ["DocumentRevisionId"] = context.GetInput<string>(documentRevisionId)!,
            ["ReviewCycleId"] = context.GetInput<string>(reviewCycleId)!,
            ["ReviewAssignmentId"] = assignmentId(context),
            ["DocumentNumber"] = context.GetInput<string>(documentNumber)!,
            ["Revision"] = context.GetInput<int>(revision),
            ["Discipline"] = "Mechanical",
            ["CandidateUsers"] = new[] { "mechanical-reviewer" },
            ["DefinitionVersionId"] = context.GetInput<string>(definitionVersionId)!
        });

        var reviewWait = new RunTask("DisciplineReview:Mechanical")
        {
            Name = "WaitForMechanicalReview",
            Payload = new(payload)
        };
        var reviewBranch = new Sequence
        {
            Name = "ReviewCompletionBranch",
            Activities =
            {
                reviewWait,
                new SetVariable<string>(status, "OnTime") { Name = "MarkReviewOnTime" },
                new Break { Name = "CancelSlaAfterReviewCompletion" }
            }
        };

        var timerBranch = new Sequence
        {
            Name = "SlaTimerBranch",
            Activities =
            {
                new Delay(context => context.GetInput<TimeSpan>(initialDelay)) { Name = "CapstoneInitialDelay" },
                new SendReviewReminderActivity
                {
                    Name = "SendCapstoneReminder",
                    ReviewKey = new(getReviewKey),
                    ReminderNumber = new(1),
                    OperationId = new(context => $"Reminder:{context.GetInput<string>(reviewKey)}:1")
                },
                new SetVariable<string>(status, "ReminderSent") { Name = "MarkCapstoneReminderSent" },
                new Delay(context => context.GetInput<TimeSpan>(reminderDelay)) { Name = "CapstoneEscalationDelay" },
                new EscalateReviewActivity
                {
                    Name = "EscalateCapstoneReview",
                    ReviewKey = new(getReviewKey),
                    OperationId = new(context => $"Escalation:{context.GetInput<string>(reviewKey)}")
                },
                new SetVariable<string>(status, "Escalated") { Name = "MarkCapstoneEscalated" },
                new Break { Name = "EndCapstoneSlaAfterEscalation" }
            }
        };

        builder.Root = new Sequence
        {
            Name = "EdmsReviewSlaRaceSequence",
            Activities =
            {
                new While(() => true)
                {
                    Name = "ReviewVersusSla",
                    Body = new Fork
                    {
                        Name = "ForkReviewAgainstSla",
                        JoinMode = ForkJoinMode.WaitAll,
                        Branches = { reviewBranch, timerBranch }
                    }
                },
                new SetOutput
                {
                    OutputName = new("FinalSlaStatus"),
                    OutputValue = new(context => getStatus(context))
                }
            }
        };
    }
}
