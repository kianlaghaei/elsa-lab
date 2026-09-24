using System.Security.Cryptography;

namespace ElsaLab.Runner.Services;

/// <summary>
/// Physical-move adapter for the EDMS fit test. Callers select logical locations,
/// never filesystem paths.
/// </summary>
public sealed class FileSystemDocumentStorageService(
    DocumentStorageOptions options,
    InMemoryDocumentRepository repository) : IDocumentStorageService
{
    private readonly string _rootPath = Path.GetFullPath(options.RootPath);

    public Task<DocumentStorageResult> MoveAsync(
        DocumentStorageCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateCommand(command);

        lock (repository.SyncRoot)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!repository.TryGetDocument(command.DocumentId, out var document) || document is null)
                throw new KeyNotFoundException($"Document '{command.DocumentId}' is not registered.");

            if (!string.Equals(document.DocumentNumber, command.DocumentNumber, StringComparison.Ordinal) ||
                document.Revision != command.Revision)
                throw new InvalidOperationException("The storage command does not match the registered document revision.");

            var sourcePath = GetDocumentPath(command.SourceLocation, command.DocumentNumber, command.Revision);
            var destinationPath = GetDocumentPath(command.DestinationLocation, command.DocumentNumber, command.Revision);

            if (repository.TryGetOperation(command.OperationId, out var existingOperation) && existingOperation is not null)
                return Task.FromResult(CompleteDuplicate(command, document, existingOperation, sourcePath, destinationPath));

            if (document.LogicalLocation != command.SourceLocation ||
                !PathsEqual(document.PhysicalPath, sourcePath))
                throw new InvalidOperationException("Document metadata does not match the requested source location.");

            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The source document file does not exist.", sourcePath);

            if (File.Exists(destinationPath))
                throw new IOException("The destination already contains a file and will not be overwritten.");

            var contentHash = GetContentHash(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath);

            var updatedDocument = document with
            {
                LogicalLocation = command.DestinationLocation,
                PhysicalPath = destinationPath,
                Status = command.DestinationLocation.ToString(),
                LastStorageOperationId = command.OperationId
            };

            // The filesystem move and metadata/journal update are separate operations;
            // this in-memory example does not make them an ACID transaction.
            repository.UpdateDocument(updatedDocument);
            repository.AddOperation(new DocumentStorageOperation(
                command.OperationId,
                command.DocumentId,
                command.DocumentNumber,
                command.Revision,
                command.SourceLocation,
                command.DestinationLocation,
                contentHash,
                destinationPath));
            repository.AddAttempt(new DocumentStorageAttempt(
                command,
                DocumentStorageOperationStatus.Applied,
                destinationPath));

            return Task.FromResult(new DocumentStorageResult(
                command.OperationId,
                DocumentStorageOperationStatus.Applied,
                command.DestinationLocation,
                destinationPath,
                updatedDocument));
        }
    }

    private DocumentStorageResult CompleteDuplicate(
        DocumentStorageCommand command,
        DocumentMetadata document,
        DocumentStorageOperation existingOperation,
        string sourcePath,
        string destinationPath)
    {
        if (!SameLogicalCommand(command, existingOperation))
            throw new InvalidOperationException(
                $"Operation ID '{command.OperationId}' is already associated with a different storage command.");

        if (File.Exists(sourcePath) ||
            !File.Exists(destinationPath) ||
            !string.Equals(GetContentHash(destinationPath), existingOperation.ContentSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The previously applied storage operation no longer matches the filesystem state.");

        if (document.LogicalLocation != command.DestinationLocation ||
            !PathsEqual(document.PhysicalPath, destinationPath) ||
            !string.Equals(document.LastStorageOperationId, command.OperationId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The previously applied storage operation no longer matches document metadata.");

        repository.AddAttempt(new DocumentStorageAttempt(
            command,
            DocumentStorageOperationStatus.AlreadyApplied,
            destinationPath));

        return new DocumentStorageResult(
            command.OperationId,
            DocumentStorageOperationStatus.AlreadyApplied,
            command.DestinationLocation,
            destinationPath,
            document);
    }

    private string GetDocumentPath(DocumentStorageLocation location, string documentNumber, int revision)
    {
        var folder = location switch
        {
            DocumentStorageLocation.Incoming => "incoming",
            DocumentStorageLocation.Approved => "approved",
            DocumentStorageLocation.RevisionRequired => "revision-required",
            _ => throw new ArgumentOutOfRangeException(nameof(location), location, "Unsupported logical storage location.")
        };

        var fileName = $"{documentNumber}-R{revision}.pdf";
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, folder, fileName));
        var rootWithSeparator = _rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved document path escaped the configured storage root.");

        return fullPath;
    }

    private static void ValidateCommand(DocumentStorageCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.OperationId))
            throw new ArgumentException("An operation ID is required.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.DocumentId))
            throw new ArgumentException("A document ID is required.", nameof(command));
        if (command.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Revision must be non-negative.");
        if (string.IsNullOrWhiteSpace(command.WorkflowInstanceId) ||
            string.IsNullOrWhiteSpace(command.ActivityId) ||
            string.IsNullOrWhiteSpace(command.ActivityExecutionId) ||
            string.IsNullOrWhiteSpace(command.ActivityName))
            throw new ArgumentException("Elsa workflow and Activity execution identity is required.", nameof(command));

        if (string.IsNullOrWhiteSpace(command.DocumentNumber) ||
            command.DocumentNumber is "." or ".." ||
            command.DocumentNumber.Contains("..", StringComparison.Ordinal) ||
            command.DocumentNumber.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Document number must be a filename-safe identifier.", nameof(command));

        if (command.SourceLocation == command.DestinationLocation)
            throw new ArgumentException("Source and destination logical locations must differ.", nameof(command));
    }

    private static bool SameLogicalCommand(DocumentStorageCommand command, DocumentStorageOperation operation) =>
        string.Equals(command.DocumentId, operation.DocumentId, StringComparison.Ordinal) &&
        string.Equals(command.DocumentNumber, operation.DocumentNumber, StringComparison.Ordinal) &&
        command.Revision == operation.Revision &&
        command.SourceLocation == operation.SourceLocation &&
        command.DestinationLocation == operation.DestinationLocation;

    private static string GetContentHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
}
