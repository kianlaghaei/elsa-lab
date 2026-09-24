using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class ReviewDisciplineActivity : CodeActivity
{
    [Input]
    public Input<string> DocumentNumber { get; set; } = null!;

    [Input]
    public Input<string> Discipline { get; set; } = null!;

    [Output]
    public Output<string> ReviewResult { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetRequiredService<IDocumentDisciplineReviewService>();
        var result = await service.ReviewAsync(
            DocumentNumber.Get(context),
            Discipline.Get(context),
            context.CancellationToken);

        ReviewResult.Set(context, result);
    }
}
