using PilahDok.Core.Models;

namespace PilahDok.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IJobStore
{
    Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken);
    Task SetAsync(JobRecord job, CancellationToken cancellationToken);
    Task<bool> TryReplaceAsync(JobRecord expected, JobRecord updated, CancellationToken cancellationToken);
    Task DeleteAsync(string jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetExpiredJobIdsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
}

public interface IJobBroker
{
    Task EnqueueAsync(JobQueueMessage message, CancellationToken cancellationToken);
    Task<JobQueueMessage?> DequeueAsync(TimeSpan wait, CancellationToken cancellationToken);
}

public interface IJobFileStore
{
    string GetInputPath(string jobId);
    string GetAnalysisPath(string jobId);
    string GetOutputDirectory(string jobId);
    string GetResultZipPath(string jobId);
    Task EnsureJobDirectoryAsync(string jobId, CancellationToken cancellationToken);
    Task SetExpiryAsync(string jobId, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> FindExpiredJobIdsAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task DeleteJobAsync(string jobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> FindOrphanedJobIdsAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}

public interface IDocxPackageValidator
{
    Task ValidateAsync(string path, CancellationToken cancellationToken);
}

public interface IAntivirusScanner
{
    Task ScanAsync(string path, CancellationToken cancellationToken);
}

public interface IDocumentEngine
{
    Task<AnalysisResult> AnalyzeAsync(string jobId, string inputPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<OutputArtifact>> SplitAsync(
        string inputPath,
        string outputDirectory,
        AnalysisResult analysis,
        SplitRequest request,
        CancellationToken cancellationToken);
    Task ValidateOutputAsync(string outputPath, CancellationToken cancellationToken);
}

public interface IWordPaginationEngine
{
    string ProviderName { get; }
    bool IsAvailable { get; }
}

public interface IJobOperationProcessor
{
    Task ProcessAsync(JobQueueMessage message, CancellationToken cancellationToken);
}

public interface IJobCleanupService
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}
