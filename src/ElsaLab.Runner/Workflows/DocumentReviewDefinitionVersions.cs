using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public static class DocumentReviewDefinitionIdentity
{
    public const string DefinitionId = "EngineeringReview";
    public const string Version1Id = "EngineeringReview-v1";
    public const string Version2Id = "EngineeringReview-v2";
}

public sealed class DocumentReviewVersion1Workflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        SetIdentity(builder, DocumentReviewDefinitionIdentity.Version1Id, 1, "Engineering Review V1");
        DefineOutputs(builder);
        builder.Root = new Sequence
        {
            Name = "EngineeringReviewV1",
            Activities =
            {
                CreatePrepareStep("PrepareReviewV1"),
                CreateWaitStep(),
                new FinalizeVersion1Activity
                {
                    Name = "FinalizeV1",
                    DefinitionMarker = new("V1")
                }
            }
        };
    }

    internal static void SetIdentity(IWorkflowBuilder builder, string versionId, int version, string name)
    {
        builder.WithDefinitionId(DocumentReviewDefinitionIdentity.DefinitionId);
        builder.WithId(versionId);
        builder.Version = version;
        builder.Name = name;
    }

    internal static void DefineOutputs(IWorkflowBuilder builder)
    {
        builder.WithOutput<string>("DefinitionMarker");
        builder.WithOutput<bool>("CoordinatorExecuted");
        builder.WithOutput<bool>("Finalized");
    }

    internal static WriteLine CreatePrepareStep(string name) => new("Preparing engineering review") { Name = name };

    internal static WaitForDocumentReviewActivity CreateWaitStep() => new()
    {
        Name = "WaitForDocumentReview",
        DocumentNumber = new("DPC-10-ME-0001"),
        Revision = new(2),
        ReviewKey = new("discipline-review")
    };
}

public sealed class DocumentReviewVersion2Workflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        DocumentReviewVersion1Workflow.SetIdentity(builder, DocumentReviewDefinitionIdentity.Version2Id, 2, "Engineering Review V2");
        DocumentReviewVersion1Workflow.DefineOutputs(builder);
        builder.Root = new Sequence
        {
            Name = "EngineeringReviewV2",
            Activities =
            {
                DocumentReviewVersion1Workflow.CreatePrepareStep("PrepareReviewV2"),
                DocumentReviewVersion1Workflow.CreateWaitStep(),
                new CoordinatorCheckActivity { Name = "CoordinatorCheck" },
                new FinalizeVersion2Activity { Name = "FinalizeV2" }
            }
        };
    }
}

public sealed class DocumentReviewVersion1MutationWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        DocumentReviewVersion1Workflow.SetIdentity(builder, DocumentReviewDefinitionIdentity.Version1Id, 1, "Engineering Review V1");
        DocumentReviewVersion1Workflow.DefineOutputs(builder);
        builder.Root = new Sequence
        {
            Name = "EngineeringReviewV1",
            Activities =
            {
                DocumentReviewVersion1Workflow.CreatePrepareStep("PrepareReviewV1"),
                DocumentReviewVersion1Workflow.CreateWaitStep(),
                new FinalizeVersion1Activity
                {
                    Name = "FinalizeV1",
                    DefinitionMarker = new("V1-MUTATED")
                }
            }
        };
    }
}

public sealed class FinalizeVersion1Activity : CodeActivity
{
    [Input]
    public Input<string> DefinitionMarker { get; set; } = new("V1");

    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Output["DefinitionMarker"] = DefinitionMarker.Get(context);
        context.WorkflowExecutionContext.Output["CoordinatorExecuted"] = false;
        context.WorkflowExecutionContext.Output["Finalized"] = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class CoordinatorCheckActivity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Output["CoordinatorExecuted"] = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class FinalizeVersion2Activity : CodeActivity
{
    protected override ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        context.WorkflowExecutionContext.Output["DefinitionMarker"] = "V2";
        context.WorkflowExecutionContext.Output["Finalized"] = true;
        return ValueTask.CompletedTask;
    }
}
