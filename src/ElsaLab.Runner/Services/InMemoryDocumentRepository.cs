namespace ElsaLab.Runner.Services;

/// <summary>
/// Small in-memory document metadata and operation journal used by the fit test.
/// </summary>
public sealed class InMemoryDocumentRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DocumentMetadata> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DocumentStorageOperation> _operations = new(StringComparer.Ordinal);
    private readonly List<DocumentStorageAttempt> _attempts = [];

    public void RegisterIncoming(DocumentMetadata document)
    {
        if (document.LogicalLocation != DocumentStorageLocation.Incoming)
            throw new ArgumentException("A newly registered fit-test document must be Incoming.", nameof(document));

        lock (_sync)
        {
            if (!_documents.TryAdd(document.DocumentId, document))
                throw new InvalidOperationException($"Document '{document.DocumentId}' is already registered.");
        }
    }

    public DocumentMetadata GetDocument(string documentId)
    {
        lock (_sync)
            return _documents.TryGetValue(documentId, out var document)
                ? document
                : throw new KeyNotFoundException($"Document '{documentId}' is not registered.");
    }

    public IReadOnlyList<DocumentStorageOperation> GetOperations()
    {
        lock (_sync)
            return _operations.Values.ToArray();
    }

    public IReadOnlyList<DocumentStorageAttempt> GetAttempts()
    {
        lock (_sync)
            return _attempts.ToArray();
    }

    internal object SyncRoot => _sync;

    internal bool TryGetDocument(string documentId, out DocumentMetadata? document) =>
        _documents.TryGetValue(documentId, out document);

    internal bool TryGetOperation(string operationId, out DocumentStorageOperation? operation) =>
        _operations.TryGetValue(operationId, out operation);

    internal void UpdateDocument(DocumentMetadata document) => _documents[document.DocumentId] = document;

    internal void AddOperation(DocumentStorageOperation operation) => _operations.Add(operation.OperationId, operation);

    internal void AddAttempt(DocumentStorageAttempt attempt) => _attempts.Add(attempt);
}
