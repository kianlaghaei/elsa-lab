using System.Globalization;
using System.Text.Json;
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

    [Input]
    public Input<bool> PublishReviewPayloadOnResume { get; set; } = new(false);

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
            Callback = PublishReviewPayloadOnResume.Get(context) ? OnResumeAsync : null,
            Metadata = new Dictionary<string, string>
            {
                ["ReviewKey"] = payload.ReviewKey
            }
        });

        // This Activity deliberately does not complete. Elsa suspends the workflow
        // after pending work drains, leaving this execution at the created bookmark.
        return ValueTask.CompletedTask;
    }

    private async ValueTask OnResumeAsync(ActivityExecutionContext context)
    {
        // A fresh runtime can rehydrate the bookmark payload even when the original workflow
        // input dictionary is empty. Read the persisted Elsa bookmark payload for continuation data.
        var payload = ReadPayload(context.Bookmarks.Single().Payload);
        context.WorkflowExecutionContext.Output["FinalDocumentNumber"] = payload.DocumentNumber;
        context.WorkflowExecutionContext.Output["FinalRevision"] = payload.Revision;
        context.WorkflowExecutionContext.Output["FinalReviewKey"] = payload.ReviewKey;
        await context.CompleteActivityAsync();
    }

    private static DocumentReviewBookmarkPayload ReadPayload(object? payload) => payload switch
    {
        DocumentReviewBookmarkPayload typed => typed,
        IDictionary<string, object> dictionary => new(
            (string)dictionary["documentNumber"],
            Convert.ToInt32(dictionary["revision"], CultureInfo.InvariantCulture),
            (string)dictionary["reviewKey"]),
        JsonElement json => new(
            json.GetProperty("documentNumber").GetString()!,
            json.GetProperty("revision").GetInt32(),
            json.GetProperty("reviewKey").GetString()!),
        _ => throw new InvalidOperationException($"Unexpected persisted document review payload type: {payload?.GetType().FullName ?? "null"}.")
    };
}
