namespace ElsaLab.Runner.Capstone;

public interface IEdmsCapstoneService
{
    Task ReceiveRevisionAsync(DocumentRevisionRecord revision, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentDistributionRecord>> DistributeAsync(
        string operationPrefix,
        DocumentRevisionRecord revision,
        IReadOnlyList<string> disciplines,
        DistributionMode mode,
        CancellationToken cancellationToken);
    Task<CapstoneReviewSummary> ConsolidateAsync(
        string documentId,
        string documentRevisionId,
        string reviewCycleId,
        CancellationToken cancellationToken);
    Task MarkRevisionRequiredAsync(string documentRevisionId, CancellationToken cancellationToken);
    Task MarkPublishedAsync(string documentRevisionId, string finalPath, CancellationToken cancellationToken);
    Task<TransmittalRecord> CreateTransmittalAsync(
        string operationId,
        string documentRevisionId,
        string sender,
        string recipient,
        string purpose,
        CancellationToken cancellationToken);
    Task<DocumentRevisionRecord> GetRevisionAsync(string documentRevisionId, CancellationToken cancellationToken);
    Task<string?> GetLatestReceivedRevisionIdAsync(string documentId, CancellationToken cancellationToken);
    Task<string?> GetCurrentValidRevisionIdAsync(string documentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReviewCommentRecord>> GetCommentsAsync(string documentRevisionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentDistributionRecord>> GetDistributionsAsync(string documentRevisionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TransmittalRecord>> GetTransmittalsAsync(CancellationToken cancellationToken);
    Task<TransmittalRecord?> GetTransmittalAsync(string operationId, CancellationToken cancellationToken);
}

