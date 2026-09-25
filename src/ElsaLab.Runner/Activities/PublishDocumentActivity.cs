using Elsa.Extensions;
using Elsa.Resilience;
using Elsa.Resilience.Models;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class PublishDocumentActivity : CodeActivity, IResilientActivity
{
    [Input]
    public Input<string> OperationId { get; set; } = null!;

    [Input]
    public Input<string> DocumentNumber { get; set; } = null!;

    [Input]
    public Input<int> Revision { get; set; } = null!;

    [Input]
    public Input<string> Destination { get; set; } = null!;

    [Output]
    public Output<string> PublicationStatus { get; set; } = new();

    [Output]
    public Output<string> PublishedDestination { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var command = new DocumentPublicationCommand(
            OperationId.Get(context),
            DocumentNumber.Get(context),
            Revision.Get(context),
            Destination.Get(context));
        var service = context.GetRequiredService<IDocumentPublicationService>();
        var invoker = context.GetRequiredService<IResilientActivityInvoker>();
        var result = await invoker.InvokeAsync(
            this,
            context,
            () => service.PublishAsync(command, context.CancellationToken),
            context.CancellationToken);

        PublicationStatus.Set(context, result.Status.ToString());
        PublishedDestination.Set(context, result.Destination);
    }

    public IDictionary<string, string?> CollectRetryDetails(ActivityExecutionContext context, RetryAttempt attempt) =>
        new Dictionary<string, string?>
        {
            ["attemptNumber"] = attempt.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["exceptionType"] = attempt.Exception?.GetType().FullName,
            ["exceptionMessage"] = attempt.Exception?.Message,
            ["operationId"] = OperationId.Get(context)
        };
}
