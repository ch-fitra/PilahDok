using PilahDok.Core.Models;

namespace PilahDok.Api.Contracts;

public sealed record CreateJobResponse(
    string JobId,
    string AccessToken,
    string DownloadToken,
    DateTimeOffset ExpiresAt,
    string UploadUrl);

public sealed record JobStatusResponse(
    string JobId,
    string Status,
    int Progress,
    string Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    string? OriginalFileName,
    long FileSize,
    string? SplitMethod,
    string? PublicErrorCode,
    string? PublicErrorMessage,
    bool DownloadAvailable,
    long? ResultSize);

public sealed record SplitSegmentRequest(string? StartCandidateId, string OutputFileName);

public sealed record StartSplitRequest(SplitMethod Method, IReadOnlyList<SplitSegmentRequest> Segments)
{
    public SplitRequest ToDomain() => new(Method, Segments.Select(segment =>
        new SplitSegment(segment.StartCandidateId, segment.OutputFileName)).ToArray());
}

public sealed record OperationAcceptedResponse(string JobId, string Status, string Operation);

public sealed record CapabilitiesResponse(
    IReadOnlyList<string> SupportedInputFormats,
    IReadOnlyList<string> StructuralSplitMethods,
    long MaxUploadBytes,
    int JobTtlMinutes,
    PaginationCapability VisualPagination,
    bool AntivirusEnabled);

public sealed record PaginationCapability(bool Available, string Provider, string Explanation);
