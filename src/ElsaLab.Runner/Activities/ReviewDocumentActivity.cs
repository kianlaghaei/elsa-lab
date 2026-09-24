using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace ElsaLab.Runner.Activities;

/// <summary>
/// Emits a per-review observation so repeated Activity output behavior can be inspected.
/// The loop decision and all loop-carried state remain in the workflow graph.
/// </summary>
public sealed class ReviewDocumentActivity : CodeActivity
{
    [Input]
    public Input<int> CurrentRevision { get; set; } = null!;

    [Input]
    public Input<int> ReviewRound { get; set; } = null!;

    [Input]
    public Input<string> ProcessingStatus { get; set; } = null!;

    [Output]
    public Output<string> ReviewSummary { get; set; } = new();

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var summary = $"Review round {ReviewRound.Get(context)} — Revision {CurrentRevision.Get(context)}";
        ReviewSummary.Set(context, summary);
        return ValueTask.CompletedTask;
    }
}
