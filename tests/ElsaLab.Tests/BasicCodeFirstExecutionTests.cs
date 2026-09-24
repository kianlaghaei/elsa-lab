using Elsa.Extensions;
using Elsa.Workflows;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Tests;

public class BasicCodeFirstExecutionTests
{
    [Fact]
    public async Task DocumentReceivedWorkflow_FinishesSuccessfully()
    {
        var services = new ServiceCollection();
        services.AddElsa(elsa => elsa.AddWorkflow<DocumentReceivedWorkflow>());

        using var serviceProvider = services.BuildServiceProvider();
        var workflowRunner = serviceProvider.GetRequiredService<IWorkflowRunner>();

        var result = await workflowRunner.RunAsync<DocumentReceivedWorkflow>();

        Assert.Equal(WorkflowStatus.Finished, result.WorkflowState.Status);
    }
}