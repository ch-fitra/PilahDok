namespace PilahDok.Core.Models;

public sealed record JobRecord
{
    public required string JobId { get; init; }
    public required string AccessTokenHash { get; init; }
    public required string DownloadTokenHash { get; init; }
    public JobStatus Status { get; init; } = JobStatus.Created;
    public string? OriginalFileName { get; init; }
    public string? SafeStoredFileName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public long FileSize { get; init; }
    public SplitMethod? SplitMethod { get; init; }
    public int Progress { get; init; }
    public string Stage { get; init; } = "created";
    public string? PublicErrorCode { get; init; }
    public string? PublicErrorMessage { get; init; }
    public required string InternalCorrelationId { get; init; }
    public JobOperation? PendingOperation { get; init; }
    public SplitRequest? PendingSplitRequest { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public string? ResultFileName { get; init; }
    public long? ResultSize { get; init; }
}

public sealed record JobQueueMessage(string JobId, JobOperation Operation);
