namespace ElsaLab.Runner.Services;

public interface IDocumentStorageService
{
    Task<DocumentStorageResult> MoveAsync(
        DocumentStorageCommand command,
        CancellationToken cancellationToken);
}
