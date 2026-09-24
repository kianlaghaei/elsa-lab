namespace ElsaLab.Runner.Services;

public interface IDocumentProcessingService
{
    Task<DocumentRegistrationResult> RegisterAsync(
        string documentNumber,
        int revision,
        CancellationToken cancellationToken);
}
