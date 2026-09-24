using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class MoveDocumentActivity : CodeActivity
{
    [Input]
    public Input<string> OperationId { get; set; } = null!;

    [Input]
    public Input<string> DocumentId { get; set; } = null!;

    [Input]
    public Input<string> DocumentNumber { get; set; } = null!;

    [Input]
    public Input<int> Revision { get; set; } = null!;

    [Input]
    public Input<DocumentStorageLocation> SourceLocation { get; set; } = null!;

    [Input]
    public Input<DocumentStorageLocation> DestinationLocation { get; set; } = null!;

    [Output]
    public Output<string> FinalLocation { get; set; } = new();

    [Output]
    public Output<string> OperationStatus { get; set; } = new();

    [Output]
    public Output<string> FinalPath { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetRequiredService<IDocumentStorageService>();
        var result = await service.MoveAsync(
            new DocumentStorageCommand(
                OperationId.Get(context),
                DocumentId.Get(context),
                DocumentNumber.Get(context),
                Revision.Get(context),
                SourceLocation.Get(context),
                DestinationLocation.Get(context),
                context.WorkflowExecutionContext.Id,
                context.Activity.Id,
                context.Id,
                context.Activity.Name ?? nameof(MoveDocumentActivity)),
            context.CancellationToken);

        FinalLocation.Set(context, result.FinalLocation.ToString());
        OperationStatus.Set(context, result.OperationStatus.ToString());
        FinalPath.Set(context, result.FinalPath);
    }
}
