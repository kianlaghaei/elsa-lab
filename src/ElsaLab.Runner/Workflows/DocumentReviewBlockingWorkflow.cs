using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public sealed class DocumentReviewBlockingWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var revisionInput = builder.WithInput<int>("Revision");
        var reviewKeyInput = builder.WithInput<string>("ReviewKey");
        var reviewOutcomeInput = builder.WithInput<string>("ReviewOutcome");
        var reviewerInput = builder.WithInput<string>("Reviewer");

        builder.WithOutput<string>("FinalStatus");
        builder.WithOutput<bool>("Finalized");
        builder.WithOutput<string>("ReviewOutcome");
        builder.WithOutput<string>("Reviewer");

        builder.Root = new Sequence
        {
            Name = "DocumentReviewSequence",
            Activities =
            {
                new WriteLine(context =>
                    $"Preparing review for {context.GetInput<string>(documentNumberInput)} revision {context.GetInput<int>(revisionInput)}")
                {
                    Name = "PrepareDocumentReview"
                },
                new WaitForDocumentReviewActivity
                {
                    Name = "WaitForDocumentReview",
                    DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
                    Revision = new(context => context.GetInput<int>(revisionInput)),
                    ReviewKey = new(context => context.GetInput<string>(reviewKeyInput)!)
                },
                new Sequence
                {
                    Name = "FinalizeDocument",
                    Activities =
                    {
                        new WriteLine("Finalized document review")
                        {
                            Name = "RecordReviewCompletion"
                        },
                        new SetOutput
                        {
                            OutputName = new("FinalStatus"),
                            OutputValue = new("ReviewCompleted")
                        },
                        new SetOutput
                        {
                            OutputName = new("Finalized"),
                            OutputValue = new(true)
                        },
                        new SetOutput
                        {
                            OutputName = new("ReviewOutcome"),
                            OutputValue = new(context => context.GetInput<string>(reviewOutcomeInput)!)
                        },
                        new SetOutput
                        {
                            OutputName = new("Reviewer"),
                            OutputValue = new(context => context.GetInput<string>(reviewerInput)!)
                        }
                    }
                }
            }
        };
    }
}
