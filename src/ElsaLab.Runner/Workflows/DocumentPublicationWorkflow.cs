using Elsa.Resilience.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.IncidentStrategies;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public sealed class DocumentPublicationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumber = builder.WithInput<string>("DocumentNumber");
        var revision = builder.WithInput<int>("Revision");
        var destination = builder.WithInput<string>("Destination");
        builder.WithOutput<string>("PublicationStatus");
        builder.WithOutput<string>("PublishedDestination");

        var publish = CreateActivity(documentNumber, revision, destination);
        builder.Root = new Sequence
        {
            Name = "DocumentPublicationSequence",
            Activities =
            {
                publish,
                new SetOutput
                {
                    OutputName = new("PublicationStatus"),
                    OutputValue = new(context => publish.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(PublishDocumentActivity.PublicationStatus))!)
                },
                new SetOutput
                {
                    OutputName = new("PublishedDestination"),
                    OutputValue = new(context => publish.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(PublishDocumentActivity.PublishedDestination))!)
                },
                new WriteLine("Publication workflow continued after the publication activity.")
                {
                    Name = "AfterPublication"
                }
            }
        };
    }

    internal static PublishDocumentActivity CreateActivity(
        InputDefinition documentNumber,
        InputDefinition revision,
        InputDefinition destination) => new()
    {
        Name = "PublishDocument",
        OperationId = new(context => $"Publish:{context.GetInput<string>(documentNumber)}:R{context.GetInput<int>(revision)}"),
        DocumentNumber = new(context => context.GetInput<string>(documentNumber)!),
        Revision = new(context => context.GetInput<int>(revision)),
        Destination = new(context => context.GetInput<string>(destination)!),
        CustomProperties =
        {
            ["resilienceStrategy"] = new ResilienceStrategyConfig
            {
                Mode = ResilienceStrategyConfigMode.Identifier,
                StrategyId = "document-publication"
            }
        }
    };
}

public sealed class DocumentPublicationContinueWithIncidentsWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumber = builder.WithInput<string>("DocumentNumber");
        var revision = builder.WithInput<int>("Revision");
        var destination = builder.WithInput<string>("Destination");
        builder.WithOutput<string>("PublicationStatus");
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        var publish = DocumentPublicationWorkflow.CreateActivity(documentNumber, revision, destination);
        builder.Root = new Sequence
        {
            Name = "DocumentPublicationContinueSequence",
            Activities =
            {
                publish,
                new SetOutput
                {
                    OutputName = new("PublicationStatus"),
                    OutputValue = new(context => publish.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(PublishDocumentActivity.PublicationStatus))!)
                },
                new WriteLine("Publication sequence continued with the recorded incident.")
                {
                    Name = "AfterPublication"
                }
            }
        };
    }
}
