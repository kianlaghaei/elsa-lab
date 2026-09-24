using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public class DocumentProcessingWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var revisionInput = builder.WithInput<int>("Revision");
        var requiresReviewInput = builder.WithInput<bool>("RequiresReview");
        builder.WithOutput<bool>("IsValid");
        builder.WithOutput<string>("ProcessingMessage");
        builder.WithOutput<string>("RegistrationReference");
        builder.WithOutput<string>("ProcessingStatus");

        var isValid = builder.WithVariable<bool>("IsValid", false);
        var processingMessage = builder.WithVariable<string>("ProcessingMessage", string.Empty);
        var registrationReference = builder.WithVariable<string>("RegistrationReference", string.Empty);
        var processingStatus = builder.WithVariable<string>("ProcessingStatus", string.Empty);

        var registerDocument = new RegisterDocumentActivity
        {
            Name = "RegisterDocument",
            DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
            Revision = new(context => context.GetInput<int>(revisionInput))
        };

        var captureRegistrationOutputs = new Sequence
        {
            Name = "CaptureRegistrationOutputs",
            Activities =
            {
                new SetVariable<bool>(isValid, context => registerDocument.GetOutput<bool>(
                    context.GetActivityExecutionContext()!, nameof(RegisterDocumentActivity.IsValid))!),
                new SetVariable<string>(processingMessage, context => registerDocument.GetOutput<string>(
                    context.GetActivityExecutionContext()!, nameof(RegisterDocumentActivity.ProcessingMessage))!),
                new SetVariable<string>(registrationReference, context => registerDocument.GetOutput<string>(
                    context.GetActivityExecutionContext()!, nameof(RegisterDocumentActivity.RegistrationReference))!)
            }
        };

        var decision = new FlowDecision(context => context.GetInput<bool>(requiresReviewInput))
        {
            Name = "RequiresReviewDecision"
        };

        var reviewDocument = new SetVariable<string>(processingStatus, "Reviewed")
        {
            Name = "ReviewDocument"
        };

        var autoAcceptDocument = new SetVariable<string>(processingStatus, "AutoAccepted")
        {
            Name = "AutoAcceptDocument"
        };

        var completeDocument = new Sequence
        {
            Name = "CompleteDocument",
            Activities =
            {
                new WriteLine(context =>
                    $"Completed {context.GetInput<string>(documentNumberInput)} revision {context.GetInput<int>(revisionInput)}: " +
                    $"ProcessingStatus={processingStatus.Get(context)}, IsValid={isValid.Get(context)}, " +
                    $"RegistrationReference={registrationReference.Get(context)}, " +
                    $"ProcessingMessage={processingMessage.Get(context)}"),
                new SetOutput
                {
                    OutputName = new("IsValid"),
                    OutputValue = new(context => isValid.Get(context))
                },
                new SetOutput
                {
                    OutputName = new("ProcessingMessage"),
                    OutputValue = new(context => processingMessage.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("RegistrationReference"),
                    OutputValue = new(context => registrationReference.Get(context)!)
                },
                new SetOutput
                {
                    OutputName = new("ProcessingStatus"),
                    OutputValue = new(context => processingStatus.Get(context)!)
                },
                new SetVariable<string>(Result, context => processingStatus.Get(context)!)
            }
        };

        builder.Root = new Flowchart
        {
            Name = "DocumentProcessingFlowchart",
            Start = registerDocument,
            Activities =
            {
                registerDocument,
                captureRegistrationOutputs,
                decision,
                reviewDocument,
                autoAcceptDocument,
                completeDocument
            },
            Connections =
            {
                new Connection(registerDocument, captureRegistrationOutputs)
                {
                    Source = new(registerDocument, "Done")
                },
                new Connection(captureRegistrationOutputs, decision)
                {
                    Source = new(captureRegistrationOutputs, "Done")
                },
                new Connection(decision, reviewDocument)
                {
                    Source = new(decision, "True")
                },
                new Connection(decision, autoAcceptDocument)
                {
                    Source = new(decision, "False")
                },
                new Connection(reviewDocument, completeDocument)
                {
                    Source = new(reviewDocument, "Done")
                },
                new Connection(autoAcceptDocument, completeDocument)
                {
                    Source = new(autoAcceptDocument, "Done")
                }
            }
        };
    }
}
