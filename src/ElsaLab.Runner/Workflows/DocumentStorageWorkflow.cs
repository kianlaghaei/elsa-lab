using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Workflows;

public sealed class DocumentStorageWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentIdInput = builder.WithInput<string>("DocumentId");
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var revisionInput = builder.WithInput<int>("Revision");
        var reviewDecisionInput = builder.WithInput<string>("ReviewDecision");

        builder.WithOutput<string>("FinalLocation");
        builder.WithOutput<string>("OperationStatus");
        builder.WithOutput<string>("FinalPath");
        builder.WithOutput<string>("OperationId");

        var documentReceived = new WriteLine(context =>
            $"Document received: {context.GetInput<string>(documentNumberInput)} revision {context.GetInput<int>(revisionInput)}")
        {
            Name = "DocumentReceived"
        };

        var reviewDecision = new FlowDecision(context =>
            string.Equals(context.GetInput<string>(reviewDecisionInput), "Approved", StringComparison.Ordinal))
        {
            Name = "ReviewDecision"
        };

        var approvedMove = new MoveDocumentActivity
        {
            Name = "MoveDocumentApproved",
            OperationId = new(context =>
                $"Move:{context.GetInput<string>(documentNumberInput)}:R{context.GetInput<int>(revisionInput)}:Approved"),
            DocumentId = new(context => context.GetInput<string>(documentIdInput)!),
            DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
            Revision = new(context => context.GetInput<int>(revisionInput)),
            SourceLocation = new(DocumentStorageLocation.Incoming),
            DestinationLocation = new(DocumentStorageLocation.Approved)
        };

        var approvedBranch = new Sequence
        {
            Name = "ApprovedStoragePath",
            Activities =
            {
                approvedMove,
                new SetOutput
                {
                    OutputName = new("FinalLocation"),
                    OutputValue = new(context => approvedMove.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(MoveDocumentActivity.FinalLocation))!)
                },
                new SetOutput
                {
                    OutputName = new("OperationStatus"),
                    OutputValue = new(context => approvedMove.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(MoveDocumentActivity.OperationStatus))!)
                },
                new SetOutput
                {
                    OutputName = new("FinalPath"),
                    OutputValue = new(context => approvedMove.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(MoveDocumentActivity.FinalPath))!)
                },
                new SetOutput
                {
                    OutputName = new("OperationId"),
                    OutputValue = new(context =>
                        $"Move:{context.GetInput<string>(documentNumberInput)}:R{context.GetInput<int>(revisionInput)}:Approved")
                }
            }
        };

        var revisionMove = new MoveDocumentActivity
        {
            Name = "MoveDocumentRevisionRequired",
            OperationId = new(context =>
                $"Move:{context.GetInput<string>(documentNumberInput)}:R{context.GetInput<int>(revisionInput)}:RevisionRequired"),
            DocumentId = new(context => context.GetInput<string>(documentIdInput)!),
            DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
            Revision = new(context => context.GetInput<int>(revisionInput)),
            SourceLocation = new(DocumentStorageLocation.Incoming),
            DestinationLocation = new(DocumentStorageLocation.RevisionRequired)
        };

        var revisionRequiredBranch = new Sequence
        {
            Name = "RevisionRequiredStoragePath",
            Activities =
            {
                revisionMove,
                new SetOutput
                {
                    OutputName = new("FinalLocation"),
                    OutputValue = new(context => revisionMove.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(MoveDocumentActivity.FinalLocation))!)
                },
                new SetOutput
                {
                    OutputName = new("OperationStatus"),
                    OutputValue = new(context => revisionMove.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(MoveDocumentActivity.OperationStatus))!)
                },
                new SetOutput
                {
                    OutputName = new("FinalPath"),
                    OutputValue = new(context => revisionMove.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(MoveDocumentActivity.FinalPath))!)
                },
                new SetOutput
                {
                    OutputName = new("OperationId"),
                    OutputValue = new(context =>
                        $"Move:{context.GetInput<string>(documentNumberInput)}:R{context.GetInput<int>(revisionInput)}:RevisionRequired")
                }
            }
        };

        var completeDocument = new WriteLine("Document storage and metadata update completed")
        {
            Name = "CompleteDocument"
        };

        builder.Root = new Flowchart
        {
            Name = "DocumentStorageFlowchart",
            Start = documentReceived,
            Activities =
            {
                documentReceived,
                reviewDecision,
                approvedBranch,
                revisionRequiredBranch,
                completeDocument
            },
            Connections =
            {
                new Connection(documentReceived, reviewDecision)
                {
                    Source = new(documentReceived, "Done")
                },
                new Connection(reviewDecision, approvedBranch)
                {
                    Source = new(reviewDecision, "True")
                },
                new Connection(reviewDecision, revisionRequiredBranch)
                {
                    Source = new(reviewDecision, "False")
                },
                new Connection(approvedBranch, completeDocument)
                {
                    Source = new(approvedBranch, "Done")
                },
                new Connection(revisionRequiredBranch, completeDocument)
                {
                    Source = new(revisionRequiredBranch, "Done")
                }
            }
        };
    }
}
