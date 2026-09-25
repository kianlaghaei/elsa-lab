using Elsa.Resilience;
using Elsa.Workflows;
using Polly;
using Polly.Retry;

namespace ElsaLab.Runner.Services;

public sealed class DocumentServiceTransientExceptionStrategy : ITransientExceptionStrategy
{
    public bool IsTransient(Exception exception) => exception is TransientDocumentServiceException;
}

/// <summary>
/// Demonstration policy: two retries with a short fixed delay, filtered through Elsa's
/// registered transient-exception detector. Cancellation is never treated as retryable.
/// </summary>
public sealed class DocumentPublicationRetryStrategy : IResilienceStrategy
{
    public string Id { get; set; } = "document-publication";
    public string DisplayName { get; set; } = "Document publication transient retry";
    public int MaxRetryAttempts { get; set; } = 2;
    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(5);

    public Task ConfigurePipeline<T>(ResiliencePipelineBuilder<T> pipelineBuilder, ResilienceContext context)
    {
        var activityContextKey = new ResiliencePropertyKey<ActivityExecutionContext>(nameof(ActivityExecutionContext));
        var options = new RetryStrategyOptions<T>
        {
            MaxRetryAttempts = MaxRetryAttempts,
            Delay = Delay,
            UseJitter = false,
            BackoffType = DelayBackoffType.Constant,
            ShouldHandle = args =>
            {
                var exception = args.Outcome.Exception;
                if (exception is null || args.Context.CancellationToken.IsCancellationRequested)
                    return ValueTask.FromResult(false);

                var activityContext = args.Context.Properties.GetValue(activityContextKey, defaultValue: null!);
                var detector = activityContext.GetRequiredService<ITransientExceptionDetector>();
                return ValueTask.FromResult(detector.IsTransient(exception));
            }
        };

        pipelineBuilder.AddRetry(options);
        return Task.CompletedTask;
    }
}
