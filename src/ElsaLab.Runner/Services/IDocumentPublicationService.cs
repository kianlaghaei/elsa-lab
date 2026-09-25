namespace ElsaLab.Runner.Services;

public interface IDocumentPublicationService
{
    Task<DocumentPublicationResult> PublishAsync(
        DocumentPublicationCommand command,
        CancellationToken cancellationToken);
}
