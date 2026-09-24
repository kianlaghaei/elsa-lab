namespace ElsaLab.Runner.Services;

public sealed class DocumentDisciplineReviewService : IDocumentDisciplineReviewService
{
    public Task<string> ReviewAsync(
        string documentNumber,
        string discipline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult($"{discipline} reviewed {documentNumber}");
    }

    public Task<string> ConsolidateAsync(
        string documentNumber,
        string processReview,
        string mechanicalReview,
        string instrumentReview,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (new[] { processReview, mechanicalReview, instrumentReview }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("All three discipline review results are required.");

        return Task.FromResult($"AllReviewsCompleted for {documentNumber}");
    }
}
