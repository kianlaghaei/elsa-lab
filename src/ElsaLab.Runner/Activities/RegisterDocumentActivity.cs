using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class RegisterDocumentActivity : CodeActivity
{
    [Input]
    public Input<string> DocumentNumber { get; set; } = null!;

    [Input]
    public Input<int> Revision { get; set; } = null!;

    [Output]
    public Output<bool> IsValid { get; set; } = new();

    [Output]
    public Output<string> ProcessingMessage { get; set; } = new();

    [Output]
    public Output<string> RegistrationReference { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var documentProcessingService = context.GetRequiredService<IDocumentProcessingService>();
        var result = await documentProcessingService.RegisterAsync(
            DocumentNumber.Get(context),
            Revision.Get(context),
            context.CancellationToken);

        IsValid.Set(context, result.IsValid);
        ProcessingMessage.Set(context, result.ProcessingMessage);
        RegistrationReference.Set(context, result.GeneratedReference);
    }
}
