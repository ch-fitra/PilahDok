using System.IO.Compression;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Errors;
using PilahDok.Core.Models;
using PilahDok.Core.Services;
using PilahDok.Infrastructure.Serialization;
using PilahDok.Infrastructure.Observability;

namespace PilahDok.Infrastructure.Jobs;

public sealed partial class JobOperationProcessor(
    IJobStore jobStore,
    IJobFileStore fileStore,
    IDocumentEngine documentEngine,
    IClock clock,
    PilahDokMetrics metrics,
    ILogger<JobOperationProcessor> logger) : IJobOperationProcessor
{
    public async Task ProcessAsync(JobQueueMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var job = await jobStore.GetAsync(message.JobId, cancellationToken);
            if (job is null || job.Status is JobStatus.Deleted or JobStatus.Expired)
            {
                return;
            }

            if (job.ExpiresAt <= clock.UtcNow)
            {
                await ExpireAsync(job, cancellationToken);
                return;
            }

            // Duplicate or delayed queue deliveries must not fail a completed/newer operation.
            if (job.Status != JobStatus.Queued || job.PendingOperation != message.Operation)
            {
                return;
            }

            switch (message.Operation)
            {
                case JobOperation.Analyze:
                    await AnalyzeAsync(job, cancellationToken);
                    break;
                case JobOperation.Split:
                    await SplitAsync(job, cancellationToken);
                    break;
                default:
                    throw new PilahDokException("unknown_operation", "Operasi job tidak dikenali.", 422);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await MarkFailedAsync(
                message,
                new PilahDokException(
                    "processing_timeout",
                    "Pemrosesan melewati batas waktu. Silakan coba lagi dengan dokumen yang lebih kecil.",
                    408),
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await MarkFailedAsync(message, exception, CancellationToken.None);
        }
    }

    private async Task AnalyzeAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        JobStateMachine.Ensure(job, JobStatus.Queued);
        var analyzing = JobStateMachine.Transition(job, JobStatus.Analyzing, clock.UtcNow, 30, "analyzing");
        if (!await jobStore.TryReplaceAsync(job, analyzing, cancellationToken))
        {
            return;
        }
        job = analyzing;

        var analysis = await documentEngine.AnalyzeAsync(job.JobId, fileStore.GetInputPath(job.JobId), cancellationToken);
        var analysisPath = fileStore.GetAnalysisPath(job.JobId);
        await using (var stream = new FileStream(analysisPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
        {
            await JsonSerializer.SerializeAsync(stream, analysis, JsonDefaults.Options, cancellationToken);
        }

        job = JobStateMachine.Transition(job, JobStatus.Ready, clock.UtcNow, 100, "ready") with
        {
            PendingOperation = null
        };
        if (job.ExpiresAt <= clock.UtcNow)
        {
            await ExpireAsync(job, cancellationToken);
            return;
        }
        if (!await jobStore.TryReplaceAsync(analyzing, job, cancellationToken))
        {
            await DeleteIfAbandonedAsync(job.JobId, cancellationToken);
            return;
        }

        metrics.AnalysisCompleted(Stopwatch.GetElapsedTime(startedAt));
    }

    private async Task SplitAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        JobStateMachine.Ensure(job, JobStatus.Queued);
        var request = job.PendingSplitRequest ??
                      throw new PilahDokException("missing_split_request", "Konfigurasi pemisahan tidak ditemukan.", 422);
        var processing = JobStateMachine.Transition(job, JobStatus.Processing, clock.UtcNow, 15, "processing") with
        {
            SplitMethod = request.Method
        };
        if (!await jobStore.TryReplaceAsync(job, processing, cancellationToken))
        {
            return;
        }
        job = processing;

        var analysis = await ReadAnalysisAsync(job.JobId, cancellationToken);
        var outputDirectory = fileStore.GetOutputDirectory(job.JobId);
        RecreateOutputDirectory(outputDirectory);
        var artifacts = await documentEngine.SplitAsync(
            fileStore.GetInputPath(job.JobId),
            outputDirectory,
            analysis,
            request,
            cancellationToken);

        var manifest = new ResultManifest(
            job.JobId,
            request.Method,
            clock.UtcNow,
            artifacts,
            analysis.Warnings.Concat(artifacts.SelectMany(artifact => artifact.Warnings)).Distinct().ToArray());
        var resultPath = fileStore.GetResultZipPath(job.JobId);
        await CreateResultZipAsync(resultPath, outputDirectory, manifest, cancellationToken);
        var resultInfo = new FileInfo(resultPath);

        job = JobStateMachine.Transition(job, JobStatus.Completed, clock.UtcNow, 100, "completed") with
        {
            PendingOperation = null,
            PendingSplitRequest = null,
            ResultFileName = "PilahDok-hasil.zip",
            ResultSize = resultInfo.Length
        };
        if (job.ExpiresAt <= clock.UtcNow)
        {
            await ExpireAsync(job, cancellationToken);
            return;
        }
        if (!await jobStore.TryReplaceAsync(processing, job, cancellationToken))
        {
            await DeleteIfAbandonedAsync(job.JobId, cancellationToken);
            return;
        }

        metrics.ProcessingCompleted(Stopwatch.GetElapsedTime(startedAt), resultInfo.Length);
    }

    private async Task<AnalysisResult> ReadAnalysisAsync(string jobId, CancellationToken cancellationToken)
    {
        var path = fileStore.GetAnalysisPath(jobId);
        if (!File.Exists(path))
        {
            throw new PilahDokException("analysis_not_found", "Hasil analisis belum tersedia.", 409);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await JsonSerializer.DeserializeAsync<AnalysisResult>(stream, JsonDefaults.Options, cancellationToken) ??
               throw new PilahDokException("analysis_invalid", "Hasil analisis tidak dapat dibaca.", 500);
    }

    private static async Task CreateResultZipAsync(
        string resultPath,
        string outputDirectory,
        ResultManifest manifest,
        CancellationToken cancellationToken)
    {
        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }

        await using var file = new FileStream(resultPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var artifact in manifest.Files.OrderBy(item => item.Order))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(artifact.FileName, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var source = new FileStream(
                Path.Combine(outputDirectory, artifact.FileName),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                true);
            await source.CopyToAsync(entryStream, cancellationToken);
        }

        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
        await using var manifestStream = manifestEntry.Open();
        await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonDefaults.Options, cancellationToken);
    }

    private async Task MarkFailedAsync(JobQueueMessage message, Exception exception, CancellationToken cancellationToken)
    {
        var jobId = message.JobId;
        var job = await jobStore.GetAsync(jobId, cancellationToken);
        if (job is null || job.Status is not (JobStatus.Queued or JobStatus.Analyzing or JobStatus.Processing) ||
            job.PendingOperation != message.Operation)
        {
            return;
        }

        var publicError = exception as PilahDokException;
        LogJobFailure(
            logger,
            jobId,
            exception.GetType().Name,
            publicError?.PublicCode ?? "processing_failed");
        var failed = job with
        {
            Status = JobStatus.Failed,
            UpdatedAt = clock.UtcNow,
            Progress = 100,
            Stage = "failed",
            PublicErrorCode = publicError?.PublicCode ?? "processing_failed",
            PublicErrorMessage = publicError?.PublicMessage ?? "Dokumen tidak dapat diproses. Silakan periksa file dan coba lagi.",
            PendingOperation = null,
            PendingSplitRequest = null
        };
        if (await jobStore.TryReplaceAsync(job, failed, cancellationToken))
        {
            metrics.Failure(failed.PublicErrorCode!);
        }
    }

    private async Task DeleteIfAbandonedAsync(string jobId, CancellationToken cancellationToken)
    {
        var current = await jobStore.GetAsync(jobId, cancellationToken);
        if (current is null || current.Status is JobStatus.Deleted or JobStatus.Expired || current.ExpiresAt <= clock.UtcNow)
            await fileStore.DeleteJobAsync(jobId, cancellationToken);
    }

    private async Task ExpireAsync(JobRecord job, CancellationToken cancellationToken)
    {
        await fileStore.DeleteJobAsync(job.JobId, cancellationToken);
        await jobStore.DeleteAsync(job.JobId, cancellationToken);
    }

    private static void RecreateOutputDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Error, Message = "Job {JobId} failed with type {ExceptionType} and public code {PublicCode}")]
    private static partial void LogJobFailure(ILogger logger, string jobId, string exceptionType, string publicCode);
}
