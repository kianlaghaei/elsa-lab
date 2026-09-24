using Elsa.Workflows;
using Elsa.Workflows.Activities;

namespace ElsaLab.Runner.Workflows;

public class DocumentReceivedWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Root = new Sequence
        {
            Activities =
            {
                new WriteLine("Document received"),
                new WriteLine("Document processing started")
            }
        };
    }
}