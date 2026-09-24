namespace ElsaLab.Runner.Services;

public enum DocumentStorageLocation
{
    Incoming,
    Approved,
    RevisionRequired
}

public enum DocumentStorageOperationStatus
{
    Applied,
    AlreadyApplied
}

public sealed record DocumentMetadata(
    string DocumentId,
    string DocumentNumber,
    int Revision,
    DocumentStorageLocation LogicalLocation,
    string PhysicalPath,
    string Status,
    string? LastStorageOperationId);

public sealed record DocumentStorageCommand(
    string OperationId,
    string DocumentId,
    string DocumentNumber,
    int Revision,
    DocumentStorageLocation SourceLocation,
    DocumentStorageLocation DestinationLocation,
    string WorkflowInstanceId,
    string ActivityId,
    string ActivityExecutionId,
    string ActivityName);

public sealed record DocumentStorageResult(
    string OperationId,
    DocumentStorageOperationStatus OperationStatus,
    DocumentStorageLocation FinalLocation,
    string FinalPath,
    DocumentMetadata Metadata);

public sealed record DocumentStorageOperation(
    string OperationId,
    string DocumentId,
    string DocumentNumber,
    int Revision,
    DocumentStorageLocation SourceLocation,
    DocumentStorageLocation DestinationLocation,
    string ContentSha256,
    string FinalPath);

public sealed record DocumentStorageAttempt(
    DocumentStorageCommand Command,
    DocumentStorageOperationStatus OperationStatus,
    string FinalPath);

public sealed record DocumentStorageOptions(string RootPath);
