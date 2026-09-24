using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using ElsaLab.Runner.Activities;

namespace ElsaLab.Runner.Workflows;

public class DocumentDisciplineReviewWorkflow : WorkflowBase<string>
{
    protected override void Build(IWorkflowBuilder builder)
    {
        var documentNumberInput = builder.WithInput<string>("DocumentNumber");
        builder.WithOutput<string>("ProcessReview");
        builder.WithOutput<string>("MechanicalReview");
        builder.WithOutput<string>("InstrumentReview");
        builder.WithOutput<string>("ConsolidatedStatus");

        var documentReady = new WriteLine("Document ready for discipline review")
        {
            Name = "DocumentReady"
        };

        var fork = new FlowFork
        {
            Name = "ForkDisciplineReviews",
            Branches = new(["Process", "Instrument", "Mechanical"])
        };

        var processReview = CreateReviewActivity("ReviewProcess", "Process", documentNumberInput);
        var instrumentReview = CreateReviewActivity("ReviewInstrument", "Instrument", documentNumberInput);
        var mechanicalReview = CreateReviewActivity("ReviewMechanical", "Mechanical", documentNumberInput);

        var join = new FlowJoin
        {
            Name = "JoinDisciplineReviews",
            Mode = new(FlowJoinMode.WaitAll)
        };

        var consolidateReviews = new ConsolidateReviewsActivity
        {
            Name = "ConsolidateReviews",
            DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
            ProcessReview = new(context => processReview.GetOutput<string>(
                context.GetActivityExecutionContext()!, nameof(ReviewDisciplineActivity.ReviewResult))!),
            InstrumentReview = new(context => instrumentReview.GetOutput<string>(
                context.GetActivityExecutionContext()!, nameof(ReviewDisciplineActivity.ReviewResult))!),
            MechanicalReview = new(context => mechanicalReview.GetOutput<string>(
                context.GetActivityExecutionContext()!, nameof(ReviewDisciplineActivity.ReviewResult))!)
        };

        var completeDocument = new Sequence
        {
            Name = "CompleteDocument",
            Activities =
            {
                new SetOutput
                {
                    OutputName = new("ProcessReview"),
                    OutputValue = new(context => processReview.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(ReviewDisciplineActivity.ReviewResult))!)
                },
                new SetOutput
                {
                    OutputName = new("MechanicalReview"),
                    OutputValue = new(context => mechanicalReview.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(ReviewDisciplineActivity.ReviewResult))!)
                },
                new SetOutput
                {
                    OutputName = new("InstrumentReview"),
                    OutputValue = new(context => instrumentReview.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(ReviewDisciplineActivity.ReviewResult))!)
                },
                new SetOutput
                {
                    OutputName = new("ConsolidatedStatus"),
                    OutputValue = new(context => consolidateReviews.GetOutput<string>(
                        context.GetActivityExecutionContext()!, nameof(ConsolidateReviewsActivity.ConsolidatedStatus))!)
                },
                new SetVariable<string>(Result, context =>
                    $"{context.GetInput<string>(documentNumberInput)} completed 3 discipline reviews")
            }
        };

        builder.Root = new Flowchart
        {
            Name = "DocumentDisciplineReviewFlowchart",
            Start = documentReady,
            Activities =
            {
                documentReady,
                fork,
                processReview,
                instrumentReview,
                mechanicalReview,
                join,
                consolidateReviews,
                completeDocument
            },
            Connections =
            {
                new Connection(documentReady, fork)
                {
                    Source = new(documentReady, "Done")
                },
                new Connection(fork, processReview)
                {
                    Source = new(fork, "Process")
                },
                new Connection(fork, instrumentReview)
                {
                    Source = new(fork, "Instrument")
                },
                new Connection(fork, mechanicalReview)
                {
                    Source = new(fork, "Mechanical")
                },
                new Connection(processReview, join)
                {
                    Source = new(processReview, "Done")
                },
                new Connection(instrumentReview, join)
                {
                    Source = new(instrumentReview, "Done")
                },
                new Connection(mechanicalReview, join)
                {
                    Source = new(mechanicalReview, "Done")
                },
                new Connection(join, consolidateReviews)
                {
                    Source = new(join, "Done")
                },
                new Connection(consolidateReviews, completeDocument)
                {
                    Source = new(consolidateReviews, "Done")
                }
            }
        };
    }

    private static ReviewDisciplineActivity CreateReviewActivity(
        string activityName,
        string discipline,
        Elsa.Workflows.Models.InputDefinition documentNumberInput) => new()
    {
        Name = activityName,
        DocumentNumber = new(context => context.GetInput<string>(documentNumberInput)!),
        Discipline = new(discipline)
    };
}
