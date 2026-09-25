namespace ElsaLab.Runner.Services;

public sealed record DocumentPublicationCommand(
    string OperationId,
    string DocumentNumber,
    int Revision,
    string Destination);

public enum DocumentPublicationStatus
{
    Applied,
    AlreadyApplied
}

public sealed record DocumentPublicationResult(
    string OperationId,
    DocumentPublicationStatus Status,
    string DocumentNumber,
    int Revision,
    string Destination);

public sealed class TransientDocumentServiceException(string message) : Exception(message);

public sealed class DocumentValidationException(string message) : Exception(message);
