using Elsa.Extensions;
using Elsa.Resilience;
using Elsa.Resilience.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Options;
using ElsaLab.Runner.Activities;
using ElsaLab.Runner.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ElsaLab.Runner.Services;

public static class DocumentPublicationDemo
{
    public static async Task<bool> RunAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Strategies:0:$type"] = nameof(DocumentPublicationRetryStrategy),
                ["Resilience:Strategies:0:Id"] = "document-publication",
                ["Resilience:Strategies:0:DisplayName"] = "Document publication transient retry",
                ["Resilience:Strategies:0:MaxRetryAttempts"] = "2",
                ["Resilience:Strategies:0:Delay"] = "00:00:00.005"
            })
            .Build();
        var publicationService = new TransientThenSuccessDocumentPublicationService();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDocumentPublicationService>(publicationService);
        services.AddSingleton<ITransientExceptionStrategy, DocumentServiceTransientExceptionStrategy>();
        services.AddElsa(elsa =>
        {
            elsa.UseResilience(resilience => resilience.AddResilienceStrategyType<DocumentPublicationRetryStrategy>());
            elsa.AddActivity<PublishDocumentActivity>();
            elsa.AddWorkflow<DocumentPublicationWorkflow>();
        });

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IWorkflowRunner>().RunAsync(
            new DocumentPublicationWorkflow(),
            new RunWorkflowOptions
            {
                Input = new Dictionary<string, object>
                {
                    ["DocumentNumber"] = "DPC-10-ME-0001",
                    ["Revision"] = 2,
                    ["Destination"] = "Client"
                }
            });
        var activity = result.Journal.ActivityExecutionContexts.Single(context => context.Activity is PublishDocumentActivity);
        var attempts = await scope.ServiceProvider.GetRequiredService<IRetryAttemptReader>().ReadAttemptsAsync(activity.Id);
        Console.WriteLine("ELSA-12 application service retry example");
        Console.WriteLine($"Publish DPC-10-ME-0001 revision 2 to Client: service calls={publicationService.CallCount}, retries recorded={attempts.Items.Count}");
        Console.WriteLine($"Workflow={result.WorkflowState.Status}/{result.WorkflowExecutionContext.SubStatus}; output={result.WorkflowState.Output["PublicationStatus"]}");
        return result.WorkflowState.Status == WorkflowStatus.Finished &&
               result.WorkflowExecutionContext.SubStatus == WorkflowSubStatus.Finished &&
               publicationService.CallCount == 3 &&
               attempts.Items.Count == 2 &&
               result.WorkflowState.Incidents.Count == 0;
    }
}
