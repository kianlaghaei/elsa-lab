using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;

namespace ElsaLab.Runner.Workflows;

public class DocumentProcessingWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var revisionInput = builder.WithInput<int>("Revision");
        var requiresReviewInput = builder.WithInput<bool>("RequiresReview");
        builder.WithOutput<string>("ProcessingStatus");

        var processingStatus = builder.WithVariable<string>("ProcessingStatus", "Received");
        var stepCount = builder.WithVariable<int>("StepCount", 0);

        builder.Root = new Sequence
        {
            Activities =
            {
                new WriteLine(context =>
                    $"Inputs read: DocumentNumber={context.GetInput<string>(documentNumberInput)}, " +
                    $"Revision={context.GetInput<int>(revisionInput)}, " +
                    $"RequiresReview={context.GetInput<bool>(requiresReviewInput)}"),
                new SetVariable<string>(processingStatus, context =>
                    $"Processing {context.GetInput<string>(documentNumberInput)} revision {context.GetInput<int>(revisionInput)}"),
                new SetVariable<int>(stepCount, context => stepCount.Get(context)! + 1),
                new SetVariable<string>(processingStatus, context =>
                    $"Processed {context.GetInput<string>(documentNumberInput)} revision {context.GetInput<int>(revisionInput)}"),
                new SetVariable<int>(stepCount, context => stepCount.Get(context)! + 1),
                new SetOutput
                {
                    OutputName = new("ProcessingStatus"),
                    OutputValue = new(context => processingStatus.Get(context)!)
                },
                new WriteLine(context =>
                    $"Later step observed: ProcessingStatus={processingStatus.Get(context)}, " +
                    $"StepCount={stepCount.Get(context)}, " +
                    $"RequiresReview={context.GetInput<bool>(requiresReviewInput)}"),
                new SetVariable<string>(Result, context =>
                    $"{processingStatus.Get(context)}; " +
                    $"RequiresReview={context.GetInput<bool>(requiresReviewInput)}; " +
                    $"StepCount={stepCount.Get(context)}")
            }
        };
    }
}
