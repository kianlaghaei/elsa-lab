using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public class DocumentProcessingWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        var revisionInput = builder.WithInput<int>("Revision");
        builder.WithOutput<bool>("IsValid");
        builder.WithOutput<string>("ProcessingMessage");
        builder.WithOutput<string>("RegistrationReference");

        var isValid = builder.WithVariable<bool>("IsValid", false);
        var processingMessage = builder.WithVariable<string>("ProcessingMessage", string.Empty);
        var registrationReference = builder.WithVariable<string>("RegistrationReference", string.Empty);

        var registerDocument = new RegisterDocumentActivity
        {
            DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
            Revision = new(context => context.GetInput<int>(revisionInput))
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                registerDocument,
                new SetVariable<bool>(isValid, context =>
                    registerDocument.GetOutput<bool>(context.GetActivityExecutionContext()!, nameof(RegisterDocumentActivity.IsValid))!),
                new SetVariable<string>(processingMessage, context =>
                    registerDocument.GetOutput<string>(context.GetActivityExecutionContext()!, nameof(RegisterDocumentActivity.ProcessingMessage))!),
                new SetVariable<string>(registrationReference, context =>
                    registerDocument.GetOutput<string>(context.GetActivityExecutionContext()!, nameof(RegisterDocumentActivity.RegistrationReference))!),
                new WriteLine(context =>
                    $"Downstream step consumed registration output: " +
                    $"DocumentNumber={context.GetInput<string>(documentNumberInput)}, " +
                    $"Revision={context.GetInput<int>(revisionInput)}, " +
                    $"IsValid={isValid.Get(context)}, " +
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
                new SetVariable<string>(Result, context => registrationReference.Get(context)!)
            }
        };
    }
}
