using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public sealed class DocumentReviewBlockingWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var revisionInput = builder.WithInput<int>("Revision");
        var reviewKeyInput = builder.WithInput<string>("ReviewKey");

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
                new WriteLine(context =>
                    $"Finalized {context.GetInput<string>(documentNumberInput)} revision {context.GetInput<int>(revisionInput)}")
                {
                    Name = "FinalizeDocument"
                }
            }
        };
    }
}
