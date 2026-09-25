using System.Security.Cryptography;
using ElsaLab.Runner.Services;

namespace ElsaLab.Runner.Capstone;

/// <summary>
/// Small EDMS application-service test adapter. Elsa state is intentionally stored elsewhere.
/// </summary>
public sealed class InMemoryEdmsCapstoneService : IEdmsCapstoneService
{
    private readonly IEdmsReviewTaskStore _taskStore;
    private readonly string? _storageRoot;

    public InMemoryEdmsCapstoneService(IEdmsReviewTaskStore taskStore, DocumentStorageOptions? storageOptions = null)
    {
        _taskStore = taskStore;
        _storageRoot = storageOptions is null ? null : Path.GetFullPath(storageOptions.RootPath);
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, DocumentRevisionRecord> _revisions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _latestReceived = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _currentValid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewCycleRecord> _cycles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewCommentRecord> _comments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DocumentDistributionRecord> _distributions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TransmittalRecord> _transmittals = new(StringComparer.Ordinal);

    public Task ReceiveRevisionAsync(DocumentRevisionRecord revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_revisions.TryGetValue(revision.DocumentRevisionId, out var existing))
            {
                if (existing.DocumentId != revision.DocumentId || existing.DocumentNumber != revision.DocumentNumber || existing.Revision != revision.Revision)
                    throw new InvalidOperationException($"Revision ID '{revision.DocumentRevisionId}' was reused for different document data.");
                return Task.CompletedTask;
            }

            if (_latestReceived.TryGetValue(revision.DocumentId, out var latestId) &&
                _revisions.TryGetValue(latestId, out var latest) && revision.Revision <= latest.Revision)
                throw new InvalidOperationException("A received document revision must be newer than the current latest received revision.");

            _revisions.Add(revision.DocumentRevisionId, revision);
            _latestReceived[revision.DocumentId] = revision.DocumentRevisionId;
            _cycles.TryAdd(revision.ReviewCycleId,
                new ReviewCycleRecord(revision.ReviewCycleId, revision.DocumentRevisionId, "InReview", DateTimeOffset.UtcNow, null));
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentDistributionRecord>> DistributeAsync(
        string operationPrefix,
        DocumentRevisionRecord revision,
        IReadOnlyList<string> disciplines,
        DistributionMode mode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sharedMovePath = mode == DistributionMode.Move
            ? PrepareMoveDistribution(revision, cancellationToken)
            : null;
        var fileHash = mode switch
        {
            DistributionMode.Reference => null,
            DistributionMode.Copy => GetRevisionHash(revision, cancellationToken),
            DistributionMode.Move => GetHash(sharedMovePath!),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported distribution mode.")
        };
        lock (_sync)
        {
            var results = new List<DocumentDistributionRecord>(disciplines.Count);
            foreach (var discipline in disciplines)
            {
                var workspace = $"{revision.ProjectId}/{discipline}";
                var operationId = $"{operationPrefix}:{revision.DocumentRevisionId}:{discipline}";
                var identity = mode switch
                {
                    DistributionMode.Reference => $"documents/{revision.DocumentId}/revisions/{revision.DocumentRevisionId}/canonical",
                    DistributionMode.Copy => PrepareCopyDistribution(revision, discipline, fileHash!, cancellationToken),
                    DistributionMode.Move => sharedMovePath!,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported distribution mode.")
                };
                var distribution = new DocumentDistributionRecord(
                    operationId,
                    revision.DocumentRevisionId,
                    discipline,
                    workspace,
                    mode,
                    identity);

                if (_distributions.TryGetValue(operationId, out var existing) && existing != distribution)
                    throw new InvalidOperationException($"Distribution operation '{operationId}' was reused for a different command.");
                _distributions.TryAdd(operationId, distribution);
                results.Add(_distributions[operationId]);
            }
            return Task.FromResult<IReadOnlyList<DocumentDistributionRecord>>(results);
        }
    }

    public async Task<CapstoneReviewSummary> ConsolidateAsync(
        string documentId,
        string documentRevisionId,
        string reviewCycleId,
        CancellationToken cancellationToken)
    {
        var tasks = await _taskStore.FindByReviewCycleAsync(reviewCycleId, cancellationToken);
        if (tasks.Count != 3 || tasks.Any(task => task.Status != ReviewTaskStatus.Completed || task.Decision is null))
            throw new InvalidOperationException("Consolidation requires three completed discipline review tasks.");

        var comments = tasks.Where(task => task.Decision == ReviewDecision.Commented)
            .Select(task => new ReviewCommentRecord(
                $"Comment:{task.ReviewAssignmentId}",
                documentId,
                documentRevisionId,
                reviewCycleId,
                task.Discipline,
                task.CommentText ?? throw new InvalidOperationException("A commented review has no comment text."),
                "Open",
                Array.Empty<string>()))
            .ToArray();

        lock (_sync)
        {
            foreach (var comment in comments)
            {
                if (_comments.TryGetValue(comment.CommentId, out var existing) && existing != comment)
                    throw new InvalidOperationException($"Comment ID '{comment.CommentId}' was reused for different text.");
                _comments.TryAdd(comment.CommentId, comment);
            }

            var status = comments.Length > 0 ? "RevisionRequired" : "Approved";
            _cycles[reviewCycleId] = _cycles[reviewCycleId] with { Status = status, CompletedAt = DateTimeOffset.UtcNow };
            var revision = GetRevisionUnsafe(documentRevisionId);
            _revisions[documentRevisionId] = revision with { LogicalStatus = status };
            return new CapstoneReviewSummary(comments.Length > 0, comments, status);
        }
    }

    public Task MarkRevisionRequiredAsync(string documentRevisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var revision = GetRevisionUnsafe(documentRevisionId);
            _revisions[documentRevisionId] = revision with { LogicalStatus = "RevisionRequired" };
        }
        return Task.CompletedTask;
    }

    public Task MarkPublishedAsync(string documentRevisionId, string finalPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(finalPath)));
        lock (_sync)
        {
            var revision = GetRevisionUnsafe(documentRevisionId);
            _revisions[documentRevisionId] = revision with
            {
                LogicalStatus = "Published",
                FilePath = finalPath,
                FileSha256 = hash,
                IsValid = true
            };
            _currentValid[revision.DocumentId] = documentRevisionId;
        }
        return Task.CompletedTask;
    }

    public Task<TransmittalRecord> CreateTransmittalAsync(
        string operationId,
        string documentRevisionId,
        string sender,
        string recipient,
        string purpose,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var revision = GetRevisionUnsafe(documentRevisionId);
            if (revision.LogicalStatus != "Published" || string.IsNullOrWhiteSpace(revision.FilePath) || string.IsNullOrWhiteSpace(revision.FileSha256))
                throw new InvalidOperationException("A transmittal item requires a published revision and immutable file snapshot.");

            var number = $"TR-{revision.DocumentNumber}-R{revision.Revision}";
            var proposed = new TransmittalRecord(
                operationId,
                number,
                sender,
                recipient,
                purpose,
                DateTimeOffset.UtcNow,
                [new TransmittalItemSnapshot(revision.DocumentId, revision.DocumentRevisionId, revision.FilePath, revision.FileSha256)]);

            if (_transmittals.TryGetValue(operationId, out var existing))
            {
                if (existing.Number != proposed.Number || existing.Sender != sender || existing.Recipient != recipient ||
                    existing.Purpose != purpose || existing.Items.Single() != proposed.Items.Single())
                    throw new InvalidOperationException($"Transmittal operation '{operationId}' was reused for a different command.");
                return Task.FromResult(existing);
            }

            _transmittals.Add(operationId, proposed);
            return Task.FromResult(proposed);
        }
    }

    public Task<DocumentRevisionRecord> GetRevisionAsync(string documentRevisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return Task.FromResult(GetRevisionUnsafe(documentRevisionId));
    }

    public Task<string?> GetLatestReceivedRevisionIdAsync(string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return Task.FromResult(_latestReceived.GetValueOrDefault(documentId));
    }

    public Task<string?> GetCurrentValidRevisionIdAsync(string documentId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            return Task.FromResult(_currentValid.GetValueOrDefault(documentId));
    }

    public Task<IReadOnlyList<ReviewCommentRecord>> GetCommentsAsync(string documentRevisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<ReviewCommentRecord> comments = _comments.Values
                .Where(comment => comment.DocumentRevisionId == documentRevisionId)
                .OrderBy(comment => comment.CommentId, StringComparer.Ordinal).ToArray();
            return Task.FromResult(comments);
        }
    }

    public Task<IReadOnlyList<DocumentDistributionRecord>> GetDistributionsAsync(string documentRevisionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<DocumentDistributionRecord> distributions = _distributions.Values
                .Where(item => item.DocumentRevisionId == documentRevisionId)
                .OrderBy(item => item.Discipline, StringComparer.Ordinal).ToArray();
            return Task.FromResult(distributions);
        }
    }

    public Task<IReadOnlyList<TransmittalRecord>> GetTransmittalsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<TransmittalRecord> transmittals = _transmittals.Values.OrderBy(item => item.Number, StringComparer.Ordinal).ToArray();
            return Task.FromResult(transmittals);
        }
    }

    public Task<TransmittalRecord?> GetTransmittalAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _transmittals.TryGetValue(operationId, out var record);
            return Task.FromResult(record);
        }
    }

    private DocumentRevisionRecord GetRevisionUnsafe(string revisionId) =>
        _revisions.TryGetValue(revisionId, out var revision)
            ? revision
            : throw new KeyNotFoundException($"Document revision '{revisionId}' was not found.");

    private string PrepareMoveDistribution(DocumentRevisionRecord revision, CancellationToken cancellationToken)
    {
        var root = _storageRoot ?? throw new InvalidOperationException("A storage root is required for Copy/Move distributions.");
        var sourcePath = GetIncomingPath(revision);
        var destinationPath = Path.Combine(root, "workspaces", SafeSegment(revision.ProjectId), "shared", $"{SafeSegment(revision.DocumentNumber)}-R{revision.Revision}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(destinationPath))
        {
            if (File.Exists(sourcePath) && !string.Equals(GetHash(destinationPath), GetRevisionHash(revision, cancellationToken), StringComparison.Ordinal))
                throw new InvalidOperationException("The existing shared distribution does not match the requested Move operation.");
            return destinationPath;
        }

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The canonical file for Move distribution was not found.", sourcePath);
        File.Move(sourcePath, destinationPath);
        return destinationPath;
    }

    private string PrepareCopyDistribution(DocumentRevisionRecord revision, string discipline, string expectedHash, CancellationToken cancellationToken)
    {
        var root = _storageRoot ?? throw new InvalidOperationException("A storage root is required for Copy/Move distributions.");
        var sourcePath = GetIncomingPath(revision);
        var destinationPath = Path.Combine(root, "workspaces", SafeSegment(revision.ProjectId), SafeSegment(discipline), $"{SafeSegment(revision.DocumentNumber)}-R{revision.Revision}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(destinationPath))
        {
            if (!string.Equals(GetHash(destinationPath), expectedHash, StringComparison.Ordinal))
                throw new InvalidOperationException("The existing workspace copy does not match the canonical file content.");
            return destinationPath;
        }

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The canonical file for Copy distribution was not found.", sourcePath);
        File.Copy(sourcePath, destinationPath);
        return destinationPath;
    }

    private string GetRevisionHash(DocumentRevisionRecord revision, CancellationToken cancellationToken)
    {
        var sourcePath = GetIncomingPath(revision);
        var path = revision.FilePath is not null && File.Exists(revision.FilePath) ? revision.FilePath : sourcePath;
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            throw new FileNotFoundException("The canonical document file for distribution was not found.", path);
        return GetHash(path);
    }

    private string GetIncomingPath(DocumentRevisionRecord revision)
    {
        var root = _storageRoot ?? throw new InvalidOperationException("A storage root is required for filesystem distribution.");
        var path = Path.GetFullPath(Path.Combine(root, "incoming", $"{SafeSegment(revision.DocumentNumber)}-R{revision.Revision}.pdf"));
        var boundary = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The resolved incoming file path escaped the configured storage root.");
        return path;
    }

    private static string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.Contains("..", StringComparison.Ordinal) ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("A path-safe project, discipline, or document identifier is required.");
        return value;
    }

    private static string GetHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
