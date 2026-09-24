using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public class DocumentRevisionWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var initialRevisionInput = builder.WithInput<int>("InitialRevision");
        var commentRoundsInput = builder.WithInput<int>("CommentRoundsBeforeApproval");

        builder.WithOutput<int>("FinalRevision");
        builder.WithOutput<int>("ReviewRounds");
        builder.WithOutput<string>("ProcessingStatus");
        builder.WithOutput<string>("LastReviewSummary");

        var currentRevision = builder.WithVariable<int>("CurrentRevision", 0);
        var reviewRound = builder.WithVariable<int>("ReviewRound", 0);
        var processingStatus = builder.WithVariable<string>("ProcessingStatus", "Received");
        var lastReviewSummary = builder.WithVariable<string>("LastReviewSummary", string.Empty);

        var initializeRevision = new SetVariable<int>(
            currentRevision,
            context => context.GetInput<int>(initialRevisionInput))
        {
            Name = "InitializeRevision"
        };

        var markReviewing = new SetVariable<string>(processingStatus, "Reviewing")
        {
            Name = "MarkReviewing"
        };

        var reviewDocument = new ReviewDocumentActivity
        {
            Name = "ReviewDocument",
            CurrentRevision = new(context => currentRevision.Get(context)!),
            ReviewRound = new(context => reviewRound.Get(context)! + 1),
            ProcessingStatus = new(context => processingStatus.Get(context)!)
        };

        var captureReviewSummary = new Sequence
        {
            Name = "CaptureReviewSummary",
            Activities =
            {
                new SetVariable<string>(
                    lastReviewSummary,
                    context => reviewDocument.GetOutput<string>(
                        context.GetActivityExecutionContext()!,
                        nameof(ReviewDocumentActivity.ReviewSummary))!)
                {
                    Name = "CaptureLatestReviewSummary"
                },
                new WriteLine(context => lastReviewSummary.Get(context)!)
                {
                    Name = "LogReviewSummary"
                }
            }
        };

        var incrementReviewRound = new SetVariable<int>(
            reviewRound,
            context => reviewRound.Get(context)! + 1)
        {
            Name = "IncrementReviewRound"
        };

        var hasComments = new FlowDecision(context =>
            reviewRound.Get(context)! <= context.GetInput<int>(commentRoundsInput))
        {
            Name = "HasComments"
        };

        var setComments = new SetVariable<string>(processingStatus, "Comments")
        {
            Name = "SetComments"
        };

        var logComments = new WriteLine("Comments found")
        {
            Name = "LogComments"
        };

        var incrementRevision = new SetVariable<int>(
            currentRevision,
            context => currentRevision.Get(context)! + 1)
        {
            Name = "IncrementRevision"
        };

        var setRevised = new SetVariable<string>(processingStatus, "Revised")
        {
            Name = "SetRevised"
        };

        var logRevision = new WriteLine(context => $"Revision advanced to {currentRevision.Get(context)}")
        {
            Name = "LogRevision"
        };

        var setApproved = new SetVariable<string>(processingStatus, "Approved")
        {
            Name = "SetApproved"
        };

        var logApproved = new WriteLine("Approved")
        {
            Name = "LogApproved"
        };

        var completeDocument = new Sequence
        {
            Name = "CompleteDocument",
            Activities =
            {
                new WriteLine(context =>
                    $"{context.GetInput<string>(documentNumberInput)} approved at revision " +
                    $"{currentRevision.Get(context)} after {reviewRound.Get(context)} review rounds."),
                new SetOutput
                {
                    OutputName = new("FinalRevision"),
                    OutputValue = new(context => currentRevision.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("ReviewRounds"),
                    OutputValue = new(context => reviewRound.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("ProcessingStatus"),
                    OutputValue = new(context => processingStatus.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("LastReviewSummary"),
                    OutputValue = new(context => reviewDocument.GetOutput<string>(
                        context.GetActivityExecutionContext()!,
                        nameof(ReviewDocumentActivity.ReviewSummary))!)
                },
                new SetVariable<string>(Result, context =>
                    $"{context.GetInput<string>(documentNumberInput)} approved at revision " +
                    $"{currentRevision.Get(context)} after {reviewRound.Get(context)} review rounds")
            }
        };

        builder.Root = new Flowchart
        {
            Name = "DocumentRevisionFlowchart",
            Start = initializeRevision,
            Activities =
            {
                initializeRevision,
                markReviewing,
                reviewDocument,
                captureReviewSummary,
                incrementReviewRound,
                hasComments,
                setComments,
                logComments,
                incrementRevision,
                setRevised,
                logRevision,
                setApproved,
                logApproved,
                completeDocument
            },
            Connections =
            {
                new Connection(initializeRevision, markReviewing)
                {
                    Source = new(initializeRevision, "Done")
                },
                new Connection(markReviewing, reviewDocument)
                {
                    Source = new(markReviewing, "Done")
                },
                new Connection(reviewDocument, captureReviewSummary)
                {
                    Source = new(reviewDocument, "Done")
                },
                new Connection(captureReviewSummary, incrementReviewRound)
                {
                    Source = new(captureReviewSummary, "Done")
                },
                new Connection(incrementReviewRound, hasComments)
                {
                    Source = new(incrementReviewRound, "Done")
                },
                new Connection(hasComments, setComments)
                {
                    Source = new(hasComments, "True")
                },
                new Connection(hasComments, setApproved)
                {
                    Source = new(hasComments, "False")
                },
                new Connection(setComments, logComments)
                {
                    Source = new(setComments, "Done")
                },
                new Connection(logComments, incrementRevision)
                {
                    Source = new(logComments, "Done")
                },
                new Connection(incrementRevision, setRevised)
                {
                    Source = new(incrementRevision, "Done")
                },
                new Connection(setRevised, logRevision)
                {
                    Source = new(setRevised, "Done")
                },
                new Connection(logRevision, markReviewing)
                {
                    Source = new(logRevision, "Done")
                },
                new Connection(setApproved, logApproved)
                {
                    Source = new(setApproved, "Done")
                },
                new Connection(logApproved, completeDocument)
                {
                    Source = new(logApproved, "Done")
                }
            }
        };
    }
}
