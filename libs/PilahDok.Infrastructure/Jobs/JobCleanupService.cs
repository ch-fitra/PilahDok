using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.Observability;

namespace PilahDok.Infrastructure.Jobs;

public sealed partial class JobCleanupService(
    IJobStore jobStore,
    IJobFileStore fileStore,
    IJobBroker broker,
    IClock clock,
    IOptions<PilahDokOptions> options,
    PilahDokMetrics metrics,
    ILogger<JobCleanupService> logger) : IJobCleanupService
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var indexedIds = await jobStore.GetExpiredJobIdsAsync(clock.UtcNow, 500, cancellationToken);
        var diskIds = await fileStore.FindExpiredJobIdsAsync(clock.UtcNow, cancellationToken);
        var expiredIds = indexedIds.Concat(diskIds).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var jobId in expiredIds)
        {
            await DeleteAsync(jobId, cancellationToken);
        }

        var orphanThreshold = clock.UtcNow.AddMinutes(-(options.Value.JobTtlMinutes + 10));
        var orphanIds = await fileStore.FindOrphanedJobIdsAsync(orphanThreshold, cancellationToken);
        foreach (var jobId in orphanIds.Except(expiredIds, StringComparer.Ordinal))
        {
            var job = await jobStore.GetAsync(jobId, cancellationToken);
            if (job is null || job.ExpiresAt <= clock.UtcNow)
            {
                await DeleteAsync(jobId, cancellationToken);
            }
        }

        // Reconcile the queue/metadata gap and worker crashes without storing document history.
        var activeIds = await fileStore.FindOrphanedJobIdsAsync(DateTimeOffset.MaxValue, cancellationToken);
        foreach (var id in activeIds.Except(expiredIds, StringComparer.Ordinal))
        {
            var job = await jobStore.GetAsync(id, cancellationToken);
            if (job is null || job.ExpiresAt <= clock.UtcNow) continue;
            var timeout = job.Status is JobStatus.Uploading or JobStatus.Validating
                ? options.Value.UploadTimeoutSeconds
                : options.Value.ProcessingTimeoutSeconds;
            if (job.UpdatedAt.AddSeconds(timeout + 60) > clock.UtcNow) continue;
            if (job.Status == JobStatus.Queued && job.PendingOperation is { } operation)
            {
                var recovered = job with { UpdatedAt = clock.UtcNow };
                if (await jobStore.TryReplaceAsync(job, recovered, cancellationToken))
                    await broker.EnqueueAsync(new JobQueueMessage(id, operation), cancellationToken);
            }
            else if (job.Status is JobStatus.Analyzing or JobStatus.Processing or JobStatus.Uploading or JobStatus.Validating)
            {
                var failed = job with
                {
                    Status = JobStatus.Failed,
                    Stage = "failed",
                    Progress = 100,
                    UpdatedAt = clock.UtcNow,
                    PublicErrorCode = "operation_interrupted",
                    PublicErrorMessage = "Pemrosesan terhenti atau server dimulai ulang. Silakan unggah ulang dokumen.",
                    PendingOperation = null,
                    PendingSplitRequest = null
                };
                if (await jobStore.TryReplaceAsync(job, failed, cancellationToken)) metrics.Failure("operation_interrupted");
            }
        }
    }

    private async Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        try
        {
            await fileStore.DeleteJobAsync(jobId, cancellationToken);
            await jobStore.DeleteAsync(jobId, cancellationToken);
            metrics.Cleanup();
            LogCleanupCompleted(logger, jobId);
        }
        catch (Exception exception)
        {
            LogCleanupFailed(logger, jobId, exception.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Expired job {JobId} was cleaned up")]
    private static partial void LogCleanupCompleted(ILogger logger, string jobId);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Error, Message = "Cleanup failed for job {JobId} with {ExceptionType}")]
    private static partial void LogCleanupFailed(ILogger logger, string jobId, string exceptionType);
}
