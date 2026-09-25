namespace ElsaLab.Runner.Services;

/// <summary>
/// Small deterministic application-service implementation for the manual runner.
/// Production storage/publication semantics belong in the EDMS service.
/// </summary>
public sealed class InMemoryDocumentPublicationService : IDocumentPublicationService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DocumentPublicationResult> _operations = new(StringComparer.Ordinal);

    public Task<DocumentPublicationResult> PublishAsync(
        DocumentPublicationCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            if (_operations.TryGetValue(command.OperationId, out var existing))
            {
                if (existing.DocumentNumber != command.DocumentNumber ||
                    existing.Revision != command.Revision ||
                    existing.Destination != command.Destination)
                    throw new InvalidOperationException($"Publication operation '{command.OperationId}' was reused for a different command.");

                return Task.FromResult(existing with { Status = DocumentPublicationStatus.AlreadyApplied });
            }

            var applied = new DocumentPublicationResult(
                command.OperationId,
                DocumentPublicationStatus.Applied,
                command.DocumentNumber,
                command.Revision,
                command.Destination);
            _operations.Add(command.OperationId, applied);
            return Task.FromResult(applied);
        }
    }
}

/// <summary>A runner-only example of two temporary failures followed by one successful publication.</summary>
public sealed class TransientThenSuccessDocumentPublicationService : IDocumentPublicationService
{
    private readonly InMemoryDocumentPublicationService _inner = new();

    public int CallCount { get; private set; }

    public Task<DocumentPublicationResult> PublishAsync(
        DocumentPublicationCommand command,
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (CallCount <= 2)
            throw new TransientDocumentServiceException("temporary publication outage");
        return _inner.PublishAsync(command, cancellationToken);
    }
}
