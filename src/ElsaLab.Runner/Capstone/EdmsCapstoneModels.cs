namespace ElsaLab.Runner.Capstone;

public enum ReviewTaskStatus
{
    Assigned,
    Claimed,
    Completed,
    Cancelled
}

public enum ReviewDecision
{
    Approved,
    Commented
}

public enum DistributionMode
{
    Reference,
    Copy,
    Move
}

public sealed record EdmsReviewTask(
    string TaskId,
    string OperationId,
    string WorkflowInstanceId,
    string BookmarkId,
    string ActivityId,
    string ActivityExecutionId,
    string CorrelationId,
    string ProjectId,
    string DocumentId,
    string DocumentRevisionId,
    string ReviewCycleId,
    string ReviewAssignmentId,
    string DocumentNumber,
    int Revision,
    string Discipline,
    IReadOnlyList<string> CandidateUsers,
    ReviewTaskStatus Status,
    string? ClaimedBy,
    DateTimeOffset? ClaimedAt,
    ReviewDecision? Decision,
    string? CommentText,
    string? CompletedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string DefinitionVersionId);

public sealed record DocumentRevisionRecord(
    string ProjectId,
    string DocumentId,
    string DocumentRevisionId,
    string DocumentNumber,
    int Revision,
    string ReviewCycleId,
    string LogicalStatus,
    string? FilePath,
    string? FileSha256,
    bool IsValid);

public sealed record ReviewCycleRecord(
    string ReviewCycleId,
    string DocumentRevisionId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ReviewCommentRecord(
    string CommentId,
    string DocumentId,
    string DocumentRevisionId,
    string ReviewCycleId,
    string Discipline,
    string Text,
    string Status,
    IReadOnlyList<string> Responses);

public sealed record DocumentDistributionRecord(
    string OperationId,
    string DocumentRevisionId,
    string Discipline,
    string Workspace,
    DistributionMode Mode,
    string CanonicalFileIdentity);

public sealed record TransmittalItemSnapshot(
    string DocumentId,
    string DocumentRevisionId,
    string FileIdentity,
    string FileSha256);

public sealed record TransmittalRecord(
    string OperationId,
    string Number,
    string Sender,
    string Recipient,
    string Purpose,
    DateTimeOffset IssuedAt,
    IReadOnlyList<TransmittalItemSnapshot> Items);

public sealed record CapstoneReviewSummary(
    bool HasComments,
    IReadOnlyList<ReviewCommentRecord> Comments,
    string ConsolidatedStatus);

