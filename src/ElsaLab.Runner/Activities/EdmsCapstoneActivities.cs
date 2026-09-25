using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Resilience;
using Elsa.Resilience.Models;
using ElsaLab.Runner.Capstone;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Activities;

public sealed class RegisterCapstoneRevisionActivity : CodeActivity
{
    [Input] public Input<string> ProjectId { get; set; } = null!;
    [Input] public Input<string> DocumentId { get; set; } = null!;
    [Input] public Input<string> DocumentRevisionId { get; set; } = null!;
    [Input] public Input<string> DocumentNumber { get; set; } = null!;
    [Input] public Input<int> Revision { get; set; } = null!;
    [Input] public Input<string> ReviewCycleId { get; set; } = null!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetRequiredService<IEdmsCapstoneService>();
        await service.ReceiveRevisionAsync(new DocumentRevisionRecord(
            ProjectId.Get(context),
            DocumentId.Get(context),
            DocumentRevisionId.Get(context),
            DocumentNumber.Get(context),
            Revision.Get(context),
            ReviewCycleId.Get(context),
            "Incoming",
            null,
            null,
            false), context.CancellationToken);
    }
}

public sealed class DistributeCapstoneRevisionActivity : CodeActivity
{
    [Input] public Input<string> DocumentRevisionId { get; set; } = null!;
    [Input] public Input<DistributionMode> Mode { get; set; } = new(DistributionMode.Reference);

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetRequiredService<IEdmsCapstoneService>();
        var revision = await service.GetRevisionAsync(DocumentRevisionId.Get(context), context.CancellationToken);
        await service.DistributeAsync(
            "Distribute",
            revision,
            ["Process", "Mechanical", "Instrument"],
            Mode.Get(context),
            context.CancellationToken);
    }
}

public sealed class ConsolidateCapstoneReviewsActivity : Activity
{
    [Output] public Output<bool> HasComments { get; set; } = new();
    [Output] public Output<int> CommentCount { get; set; } = new();
    [Output] public Output<string> ConsolidatedStatus { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var tasks = await context.GetRequiredService<IEdmsReviewTaskStore>()
            .FindByWorkflowAsync(context.WorkflowExecutionContext.Id, context.CancellationToken);
        var task = tasks.FirstOrDefault()
            ?? throw new InvalidOperationException("No EDMS review assignments were linked to the current workflow.");
        var service = context.GetRequiredService<IEdmsCapstoneService>();
        var summary = await service.ConsolidateAsync(
            task.DocumentId,
            task.DocumentRevisionId,
            task.ReviewCycleId,
            context.CancellationToken);
        HasComments.Set(context, summary.HasComments);
        CommentCount.Set(context, summary.Comments.Count);
        ConsolidatedStatus.Set(context, summary.ConsolidatedStatus);
        context.WorkflowExecutionContext.Output["CapstoneHasComments"] = summary.HasComments;
        context.WorkflowExecutionContext.Output["CapstoneCommentCount"] = summary.Comments.Count;
        await context.CompleteActivityWithOutcomesAsync(summary.HasComments ? "CommentsFound" : "NoComments");
    }
}

/// <summary>
/// Rehydrates the EDMS business context from the three task IDs produced by Elsa RunTask
/// nodes. This keeps post-bookmark continuation independent of request-local workflow inputs.
/// </summary>
public sealed class ResolveCapstoneReviewContextActivity : CodeActivity
{
    [Output] public Output<string> ProjectId { get; set; } = new();
    [Output] public Output<string> CorrelationId { get; set; } = new();
    [Output] public Output<string> DocumentId { get; set; } = new();
    [Output] public Output<string> DocumentRevisionId { get; set; } = new();
    [Output] public Output<string> DocumentNumber { get; set; } = new();
    [Output] public Output<int> Revision { get; set; } = new();
    [Output] public Output<string> ReviewCycleId { get; set; } = new();
    [Output] public Output<string> DefinitionVersionId { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var store = context.GetRequiredService<IEdmsReviewTaskStore>();
        var tasks = (await store.FindByWorkflowAsync(context.WorkflowExecutionContext.Id, context.CancellationToken)).ToArray();
        if (tasks.Length != 3)
            throw new InvalidOperationException($"Expected three discipline review tasks for workflow '{context.WorkflowExecutionContext.Id}', found {tasks.Length}.");

        var reference = tasks[0];
        if (tasks.Any(task => task.WorkflowInstanceId != reference.WorkflowInstanceId ||
                              task.DocumentId != reference.DocumentId ||
                              task.DocumentRevisionId != reference.DocumentRevisionId ||
                              task.ReviewCycleId != reference.ReviewCycleId))
            throw new InvalidOperationException("Discipline review tasks do not belong to the same workflow, revision, and review cycle.");

        ProjectId.Set(context, reference.ProjectId);
        CorrelationId.Set(context, reference.CorrelationId);
        DocumentId.Set(context, reference.DocumentId);
        DocumentRevisionId.Set(context, reference.DocumentRevisionId);
        DocumentNumber.Set(context, reference.DocumentNumber);
        Revision.Set(context, reference.Revision);
        ReviewCycleId.Set(context, reference.ReviewCycleId);
        DefinitionVersionId.Set(context, reference.DefinitionVersionId);
        context.WorkflowExecutionContext.Output["CapstoneProjectId"] = reference.ProjectId;
        context.WorkflowExecutionContext.Output["CapstoneCorrelationId"] = reference.CorrelationId;
        context.WorkflowExecutionContext.Output["CapstoneDocumentId"] = reference.DocumentId;
        context.WorkflowExecutionContext.Output["CapstoneDocumentRevisionId"] = reference.DocumentRevisionId;
        context.WorkflowExecutionContext.Output["CapstoneDocumentNumber"] = reference.DocumentNumber;
        context.WorkflowExecutionContext.Output["CapstoneRevision"] = reference.Revision;
        context.WorkflowExecutionContext.Output["CapstoneReviewCycleId"] = reference.ReviewCycleId;
        context.WorkflowExecutionContext.Output["CapstoneDefinitionVersionId"] = reference.DefinitionVersionId;
    }
}

public sealed class MarkCapstoneRevisionRequiredActivity : CodeActivity
{
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var task = (await context.GetRequiredService<IEdmsReviewTaskStore>()
            .FindByWorkflowAsync(context.WorkflowExecutionContext.Id, context.CancellationToken)).FirstOrDefault()
            ?? throw new InvalidOperationException("No EDMS review assignments were linked to the current workflow.");
        await context.GetRequiredService<IEdmsCapstoneService>()
            .MarkRevisionRequiredAsync(task.DocumentRevisionId, context.CancellationToken);
    }
}

public sealed class MarkCapstoneRevisionPublishedActivity : CodeActivity
{
    [Input] public Input<string> DocumentRevisionId { get; set; } = null!;
    [Input] public Input<string> FinalPath { get; set; } = null!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        await context.GetRequiredService<IEdmsCapstoneService>()
            .MarkPublishedAsync(DocumentRevisionId.Get(context), FinalPath.Get(context), context.CancellationToken);
    }
}

/// <summary>
/// The approved path resolves its business context from persisted EDMS task records. This is
/// intentional: Elsa 3.8.4's tested resumer path does not rebind original workflow inputs.
/// Each side effect has a stable operation ID and is delegated to an application service.
/// </summary>
public sealed class ApproveCapstoneRevisionActivity : CodeActivity, IResilientActivity
{
    [Output] public Output<string> FinalPath { get; set; } = new();
    [Output] public Output<string> TransmittalNumber { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var tasks = await context.GetRequiredService<IEdmsReviewTaskStore>()
            .FindByWorkflowAsync(context.WorkflowExecutionContext.Id, context.CancellationToken);
        var task = tasks.FirstOrDefault()
            ?? throw new InvalidOperationException("No EDMS review assignments were linked to the current workflow.");

        var operationId = $"Move:{task.DocumentNumber}:R{task.Revision}:Approved";
        var move = await context.GetRequiredService<IDocumentStorageService>().MoveAsync(
            new DocumentStorageCommand(
                operationId,
                task.DocumentRevisionId,
                task.DocumentNumber,
                task.Revision,
                DocumentStorageLocation.Incoming,
                DocumentStorageLocation.Approved,
                context.WorkflowExecutionContext.Id,
                context.Activity.Id,
                context.Id,
                context.Activity.Name ?? nameof(ApproveCapstoneRevisionActivity)),
            context.CancellationToken);

        var publicationCommand = new DocumentPublicationCommand(
            $"Publish:{task.DocumentNumber}:R{task.Revision}", task.DocumentNumber, task.Revision, "Client");
        var publication = await context.GetRequiredService<IResilientActivityInvoker>().InvokeAsync(
            this,
            context,
            () => context.GetRequiredService<IDocumentPublicationService>()
                .PublishAsync(publicationCommand, context.CancellationToken),
            context.CancellationToken);

        if (publication.Status is not (DocumentPublicationStatus.Applied or DocumentPublicationStatus.AlreadyApplied))
            throw new InvalidOperationException($"Publication operation '{publication.OperationId}' did not succeed.");

        var service = context.GetRequiredService<IEdmsCapstoneService>();
        await service.MarkPublishedAsync(task.DocumentRevisionId, move.FinalPath, context.CancellationToken);
        var transmittal = await service.CreateTransmittalAsync(
            $"CreateTransmittal:{task.DocumentRevisionId}:{task.ReviewCycleId}",
            task.DocumentRevisionId,
            "DEMO-PROJECT-EDMS",
            "CLIENT-DEMO",
            "For Approval",
            context.CancellationToken);

        FinalPath.Set(context, move.FinalPath);
        TransmittalNumber.Set(context, transmittal.Number);
    }

    public IDictionary<string, string?> CollectRetryDetails(ActivityExecutionContext context, RetryAttempt attempt) =>
        new Dictionary<string, string?>
        {
            ["attemptNumber"] = attempt.AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["exceptionType"] = attempt.Exception?.GetType().FullName,
            ["exceptionMessage"] = attempt.Exception?.Message,
            ["workflowInstanceId"] = context.WorkflowExecutionContext.Id,
            ["activityExecutionId"] = context.Id
        };
}

public sealed class CreateCapstoneTransmittalActivity : CodeActivity
{
    [Input] public Input<string> OperationId { get; set; } = null!;
    [Input] public Input<string> DocumentRevisionId { get; set; } = null!;

    [Output] public Output<string> TransmittalNumber { get; set; } = new();

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var record = await context.GetRequiredService<IEdmsCapstoneService>().CreateTransmittalAsync(
            OperationId.Get(context),
            DocumentRevisionId.Get(context),
            "DEMO-PROJECT-EDMS",
            "CLIENT-DEMO",
            "For Approval",
            context.CancellationToken);
        TransmittalNumber.Set(context, record.Number);
    }
}

public sealed class EdmsCoordinatorCheckActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Output["CoordinatorCheckExecuted"] = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class CompleteCapstoneWorkflowActivity : CodeActivity
{
    [Input] public Input<string> DefinitionMarker { get; set; } = new("V1");

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var service = context.GetRequiredService<IEdmsCapstoneService>();
        var taskStore = context.GetRequiredService<IEdmsReviewTaskStore>();
        var tasks = await taskStore.FindByWorkflowAsync(context.WorkflowExecutionContext.Id, context.CancellationToken);
        var task = tasks.FirstOrDefault()
            ?? throw new InvalidOperationException("No EDMS review assignments were linked to the current workflow.");
        var revision = await service.GetRevisionAsync(task.DocumentRevisionId, context.CancellationToken);
        var comments = await service.GetCommentsAsync(task.DocumentRevisionId, context.CancellationToken);
        var distributions = await service.GetDistributionsAsync(task.DocumentRevisionId, context.CancellationToken);
        var transmittalOperationId = $"CreateTransmittal:{task.DocumentRevisionId}:{task.ReviewCycleId}";
        var transmittal = await service.GetTransmittalAsync(transmittalOperationId, context.CancellationToken);

        context.WorkflowExecutionContext.Output["CorrelationId"] = task.CorrelationId;
        context.WorkflowExecutionContext.Output["DocumentId"] = task.DocumentId;
        context.WorkflowExecutionContext.Output["DocumentRevisionId"] = revision.DocumentRevisionId;
        context.WorkflowExecutionContext.Output["LatestReceivedRevisionId"] = await service.GetLatestReceivedRevisionIdAsync(task.DocumentId, context.CancellationToken) ?? string.Empty;
        context.WorkflowExecutionContext.Output["CurrentValidRevisionId"] = await service.GetCurrentValidRevisionIdAsync(task.DocumentId, context.CancellationToken) ?? string.Empty;
        context.WorkflowExecutionContext.Output["ReviewTaskCount"] = tasks.Count;
        context.WorkflowExecutionContext.Output["DistributionCount"] = distributions.Count;
        context.WorkflowExecutionContext.Output["CommentCount"] = comments.Count;
        context.WorkflowExecutionContext.Output["FinalStatus"] = revision.LogicalStatus;
        context.WorkflowExecutionContext.Output["DefinitionMarker"] = DefinitionMarker.Get(context);
        context.WorkflowExecutionContext.Output["CoordinatorCheckExecuted"] = Equals(
            context.WorkflowExecutionContext.Output.GetValueOrDefault("CoordinatorCheckExecuted"), true);
        context.WorkflowExecutionContext.Output["TransmittalNumber"] = transmittal?.Number ?? string.Empty;
        context.WorkflowExecutionContext.Output["Finalized"] = true;
    }
}
