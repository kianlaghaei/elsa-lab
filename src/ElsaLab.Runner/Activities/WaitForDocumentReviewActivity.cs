using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace ElsaLab.Runner.Activities;

public sealed record DocumentReviewBookmarkPayload(string DocumentNumber, int Revision, string ReviewKey);

public sealed class WaitForDocumentReviewActivity : Activity
{
    [Input]
    public Input<string> DocumentNumber { get; set; } = null!;

    [Input]
    public Input<int> Revision { get; set; } = null!;

    [Input]
    public Input<string> ReviewKey { get; set; } = null!;

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var payload = new DocumentReviewBookmarkPayload(
            DocumentNumber.Get(context),
            Revision.Get(context),
            ReviewKey.Get(context));

        context.CreateBookmark(new CreateBookmarkArgs
        {
            BookmarkName = "DocumentReview",
            Stimulus = payload,
            IncludeActivityInstanceId = true,
            Metadata = new Dictionary<string, string>
            {
                ["ReviewKey"] = payload.ReviewKey
            }
        });

        // This Activity deliberately does not complete. Elsa suspends the workflow
        // after pending work drains, leaving this execution at the created bookmark.
        return ValueTask.CompletedTask;
    }
}
