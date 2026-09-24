using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public class DocumentProcessingWorkflowTests
{
    [Fact]
    public async Task DocumentProcessingWorkflow_ConsumesInputs_UpdatesVariables_AndReturnsResult()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.AddWorkflow<DocumentProcessingWorkflow>());

        using var serviceProvider = services.BuildServiceProvider();
        var workflowRunner = serviceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await workflowRunner.RunAsync(
            new DocumentProcessingWorkflow(),
            new RunWorkflowOptions
            {
                Input = new Dictionary<string, object>
                {
                    ["DocumentNumber"] = "DPC-10-ME-0001",
                    ["Revision"] = 2,
                    ["RequiresReview"] = true
                }
            });

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
        Assert.Empty(result.WorkflowState.Input);
        Assert.Equal(
            "Processed DPC-10-ME-0001 revision 2; RequiresReview=True; StepCount=2",
            result.Result);
        Assert.Equal(
            "Processed DPC-10-ME-0001 revision 2",
            result.WorkflowState.Output["ProcessingStatus"]);

        var executionContext = result.WorkflowExecutionContext;
        Assert.Equal("DPC-10-ME-0001", executionContext.Input["DocumentNumber"]);
        Assert.Equal(2, executionContext.Input["Revision"]);
        Assert.Equal(true, executionContext.Input["RequiresReview"]);

        var finalActivityContext = result.Journal.ActivityExecutionContexts.Last();
        var expressionContext = finalActivityContext.ExpressionExecutionContext;
        Assert.Equal("Processed DPC-10-ME-0001 revision 2", expressionContext.GetVariable<string>("ProcessingStatus"));
        Assert.Equal(2, expressionContext.GetVariable<int>("StepCount"));
    }
}
