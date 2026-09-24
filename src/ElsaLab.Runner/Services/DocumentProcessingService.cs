namespace ElsaLab.Runner.Services;

public sealed class DocumentProcessingService : IDocumentProcessingService
{
    public Task<DocumentRegistrationResult> RegisterAsync(
        string documentNumber,
        int revision,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var isValid = !string.IsNullOrWhiteSpace(documentNumber) && revision > 0;
        var generatedReference = isValid ? $"REG-{documentNumber}-R{revision}" : string.Empty;
        var message = isValid
            ? $"Registered {documentNumber}, revision {revision}."
            : "A document number and a positive revision are required.";

        return Task.FromResult(new DocumentRegistrationResult(isValid, message, generatedReference));
    }
}
