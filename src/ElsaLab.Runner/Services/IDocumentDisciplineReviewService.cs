namespace ElsaLab.Runner.Services;

public interface IDocumentDisciplineReviewService
{
    Task<string> ReviewAsync(
        string documentNumber,
        string discipline,
        CancellationToken cancellationToken);

    Task<string> ConsolidateAsync(
        string documentNumber,
        string processReview,
        string mechanicalReview,
        string instrumentReview,
        CancellationToken cancellationToken);
}
