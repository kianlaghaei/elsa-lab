using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class ConsolidateReviewsActivity : CodeActivity
{
    [Input]
    public Input<string> DocumentNumber { get; set; } = null!;

    [Input]
    public Input<string> ProcessReview { get; set; } = null!;

    [Input]
    public Input<string> MechanicalReview { get; set; } = null!;

    [Input]
    public Input<string> InstrumentReview { get; set; } = null!;

    [Output]
    public Output<string> ConsolidatedStatus { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetRequiredService<IDocumentDisciplineReviewService>();
        var result = await service.ConsolidateAsync(
            DocumentNumber.Get(context),
            ProcessReview.Get(context),
            MechanicalReview.Get(context),
            InstrumentReview.Get(context),
            context.CancellationToken);

        ConsolidatedStatus.Set(context, result);
    }
}
