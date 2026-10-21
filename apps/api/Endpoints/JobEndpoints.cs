using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Options;
using PilahDok.Api.Contracts;
using PilahDok.Api.Security;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Errors;
using PilahDok.Core.Models;
using PilahDok.Core.Services;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.Observability;
using PilahDok.Infrastructure.Serialization;

namespace PilahDok.Api.Endpoints;

public static class JobEndpoints
{
    private const string AccessTokenHeader = "X-Job-Token";
    private const string DownloadTokenHeader = "X-Download-Token";
    private const string FileNameHeader = "X-File-Name";

    public static RouteGroupBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/jobs").WithTags("Jobs");
        group.MapPost("/", CreateJobAsync).RequireRateLimiting("api");
        group.MapPut("/{jobId}/document", UploadAsync)
            .RequireRateLimiting("upload")
            .WithRequestTimeout("upload")
            .DisableAntiforgery();
        group.MapPost("/{jobId}/analyze", AnalyzeAsync).RequireRateLimiting("api");
        group.MapGet("/{jobId}", GetStatusAsync).RequireRateLimiting("status");
        group.MapGet("/{jobId}/analysis", GetAnalysisAsync).RequireRateLimiting("status");
        group.MapPost("/{jobId}/split", SplitAsync).RequireRateLimiting("api");
        group.MapGet("/{jobId}/download", DownloadAsync).RequireRateLimiting("download");
        group.MapDelete("/{jobId}", DeleteAsync).RequireRateLimiting("api");
        return group;
    }

    private static async Task<IResult> CreateJobAsync(
        HttpContext context,
        IJobStore jobStore,
        IJobFileStore fileStore,
        IClock clock,
        IOptions<PilahDokOptions> options,
        CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var accessToken = SecureToken.Generate();
        var downloadToken = SecureToken.Generate();
        var jobId = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var job = new JobRecord
        {
            JobId = jobId,
            AccessTokenHash = SecureToken.Hash(accessToken),
            DownloadTokenHash = SecureToken.Hash(downloadToken),
            Status = JobStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.AddMinutes(options.Value.JobTtlMinutes),
            Progress = 0,
            Stage = "created",
            InternalCorrelationId = context.TraceIdentifier
        };
        await fileStore.EnsureJobDirectoryAsync(jobId, cancellationToken);
        await fileStore.SetExpiryAsync(jobId, job.ExpiresAt, cancellationToken);
        await jobStore.SetAsync(job, cancellationToken);

        context.Response.Headers.CacheControl = "no-store";
        var response = new CreateJobResponse(
            jobId,
            accessToken,
            downloadToken,
            job.ExpiresAt,
            $"/api/v1/jobs/{jobId}/document");
        return Results.Created($"/api/v1/jobs/{jobId}", response);
    }

    private static async Task<IResult> UploadAsync(
        string jobId,
        HttpContext context,
        JobAuthorization authorization,
        IJobStore jobStore,
        IJobFileStore fileStore,
        IDocxPackageValidator validator,
        IAntivirusScanner antivirus,
        IClock clock,
        IOptions<PilahDokOptions> options,
        PilahDokMetrics metrics,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireAccessAsync(jobId, context.Request.Headers[AccessTokenHeader].FirstOrDefault(), cancellationToken);
        JobStateMachine.Ensure(job, JobStatus.Created, JobStatus.Uploaded, JobStatus.Ready, JobStatus.Failed);
        var originalName = DecodeFileName(context.Request.Headers[FileNameHeader].FirstOrDefault());
        ValidateUploadHeaders(context.Request, originalName, options.Value.MaxUploadBytes);

        var uploading = JobStateMachine.Transition(job, JobStatus.Uploading, clock.UtcNow, 5, "uploading") with
        {
            OriginalFileName = SafeFileName.Sanitize(originalName),
            SafeStoredFileName = "source.docx",
            FileSize = 0,
            SplitMethod = null,
            ResultFileName = null,
            ResultSize = null,
            PendingOperation = null,
            PendingSplitRequest = null
        };
        await ReplaceJobAsync(jobStore, job, uploading, cancellationToken);
        job = uploading;

        var inputPath = fileStore.GetInputPath(jobId);
        try
        {
            await fileStore.DeleteJobAsync(jobId, cancellationToken);
            await fileStore.EnsureJobDirectoryAsync(jobId, cancellationToken);
            await fileStore.SetExpiryAsync(jobId, job.ExpiresAt, cancellationToken);
            var bytes = await StreamUploadAsync(context.Request.Body, inputPath, options.Value.MaxUploadBytes, cancellationToken);
            var validating = JobStateMachine.Transition(job, JobStatus.Validating, clock.UtcNow, 70, "validating") with { FileSize = bytes };
            await ReplaceJobAsync(jobStore, job, validating, cancellationToken);
            job = validating;
            await validator.ValidateAsync(inputPath, cancellationToken);
            await antivirus.ScanAsync(inputPath, cancellationToken);
            var uploaded = JobStateMachine.Transition(job, JobStatus.Uploaded, clock.UtcNow, 100, "uploaded");
            await ReplaceJobAsync(jobStore, job, uploaded, cancellationToken);
            job = uploaded;
            metrics.UploadCompleted(bytes);
            return Results.Ok(ToStatus(job));
        }
        catch (Exception exception)
        {
            if (File.Exists(inputPath))
            {
                File.Delete(inputPath);
            }

            var publicError = exception as PilahDokException;
            var failed = job with
            {
                Status = JobStatus.Failed,
                UpdatedAt = clock.UtcNow,
                Progress = 100,
                Stage = "failed",
                PublicErrorCode = publicError?.PublicCode ?? "upload_failed",
                PublicErrorMessage = publicError?.PublicMessage ?? "Upload tidak dapat diproses."
            };
            if (!await jobStore.TryReplaceAsync(job, failed, CancellationToken.None))
            {
                await fileStore.DeleteJobAsync(jobId, CancellationToken.None);
            }
            metrics.Failure(failed.PublicErrorCode!);
            throw;
        }
    }

    private static async Task<IResult> AnalyzeAsync(
        string jobId,
        HttpContext context,
        JobAuthorization authorization,
        IJobStore jobStore,
        IJobBroker queue,
        IJobFileStore fileStore,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireAccessAsync(jobId, context.Request.Headers[AccessTokenHeader].FirstOrDefault(), cancellationToken);
        JobStateMachine.Ensure(job, JobStatus.Uploaded, JobStatus.Failed);
        if (!File.Exists(fileStore.GetInputPath(jobId)))
        {
            throw new PilahDokException("document_not_uploaded", "Unggah dokumen valid terlebih dahulu.", 409);
        }

        var queued = JobStateMachine.Transition(job, JobStatus.Queued, clock.UtcNow, 10, "queued") with
        {
            PendingOperation = JobOperation.Analyze
        };
        await ReplaceJobAsync(jobStore, job, queued, cancellationToken);
        await queue.EnqueueAsync(new JobQueueMessage(jobId, JobOperation.Analyze), cancellationToken);
        return Results.Accepted($"/api/v1/jobs/{jobId}", new OperationAcceptedResponse(jobId, "queued", "analyze"));
    }

    private static async Task<IResult> GetStatusAsync(
        string jobId,
        HttpContext context,
        JobAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireAccessAsync(jobId, context.Request.Headers[AccessTokenHeader].FirstOrDefault(), cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(ToStatus(job));
    }

    private static async Task<IResult> GetAnalysisAsync(
        string jobId,
        HttpContext context,
        JobAuthorization authorization,
        IJobFileStore fileStore,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireAccessAsync(jobId, context.Request.Headers[AccessTokenHeader].FirstOrDefault(), cancellationToken);
        JobStateMachine.Ensure(job, JobStatus.Ready, JobStatus.Completed);
        var path = fileStore.GetAnalysisPath(jobId);
        if (!File.Exists(path))
        {
            throw new PilahDokException("analysis_not_found", "Hasil analisis belum tersedia.", 404);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var analysis = await JsonSerializer.DeserializeAsync<AnalysisResult>(stream, JsonDefaults.Options, cancellationToken) ??
                       throw new PilahDokException("analysis_invalid", "Hasil analisis tidak dapat dibaca.", 500);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(analysis);
    }

    private static async Task<IResult> SplitAsync(
        string jobId,
        [FromBody] StartSplitRequest request,
        HttpContext context,
        JobAuthorization authorization,
        IJobStore jobStore,
        IJobBroker queue,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireAccessAsync(jobId, context.Request.Headers[AccessTokenHeader].FirstOrDefault(), cancellationToken);
        JobStateMachine.Ensure(job, JobStatus.Ready, JobStatus.Completed);
        if (!Enum.IsDefined(request.Method) || request.Segments is null || request.Segments.Count is < 1 or > 200 ||
            request.Segments.Any(segment => segment is null || string.IsNullOrWhiteSpace(segment.OutputFileName) || segment.OutputFileName.Length > 140))
        {
            throw new PilahDokException("invalid_segments", "Jumlah bagian harus antara 1 dan 200.", 422);
        }

        var domainRequest = request.ToDomain();
        var queued = JobStateMachine.Transition(job, JobStatus.Queued, clock.UtcNow, 5, "queued") with
        {
            PendingOperation = JobOperation.Split,
            PendingSplitRequest = domainRequest,
            SplitMethod = request.Method
        };
        await ReplaceJobAsync(jobStore, job, queued, cancellationToken);
        await queue.EnqueueAsync(new JobQueueMessage(jobId, JobOperation.Split), cancellationToken);
        return Results.Accepted($"/api/v1/jobs/{jobId}", new OperationAcceptedResponse(jobId, "queued", "split"));
    }

    private static async Task<IResult> DownloadAsync(
        string jobId,
        HttpContext context,
        JobAuthorization authorization,
        IJobStore jobStore,
        IJobFileStore fileStore,
        IClock clock,
        IOptions<PilahDokOptions> options,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireDownloadAsync(jobId, context.Request.Headers[DownloadTokenHeader].FirstOrDefault(), cancellationToken);
        JobStateMachine.Ensure(job, JobStatus.Completed);
        var path = fileStore.GetResultZipPath(jobId);
        if (!File.Exists(path))
        {
            throw new PilahDokException("result_not_found", "File hasil tidak ditemukan.", 404);
        }

        var graceExpiry = clock.UtcNow.AddMinutes(options.Value.DownloadGraceMinutes);
        var downloaded = job with
        {
            DownloadedAt = clock.UtcNow,
            ExpiresAt = graceExpiry < job.ExpiresAt ? graceExpiry : job.ExpiresAt,
            UpdatedAt = clock.UtcNow
        };
        await ReplaceJobAsync(jobStore, job, downloaded, cancellationToken);
        await fileStore.SetExpiryAsync(jobId, downloaded.ExpiresAt, cancellationToken);
        context.Response.Headers.CacheControl = "private, no-store, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        return Results.File(
            path,
            "application/zip",
            job.ResultFileName ?? "PilahDok-hasil.zip",
            enableRangeProcessing: true);
    }

    private static async Task<IResult> DeleteAsync(
        string jobId,
        HttpContext context,
        JobAuthorization authorization,
        IJobStore jobStore,
        IJobFileStore fileStore,
        IClock clock,
        CancellationToken cancellationToken)
    {
        var job = await authorization.RequireAccessAsync(jobId, context.Request.Headers[AccessTokenHeader].FirstOrDefault(), cancellationToken);
        var deleted = job with { Status = JobStatus.Deleted, Stage = "deleted", UpdatedAt = clock.UtcNow, Progress = 100 };
        await jobStore.SetAsync(deleted, cancellationToken);
        await fileStore.DeleteJobAsync(jobId, cancellationToken);
        await jobStore.DeleteAsync(jobId, cancellationToken);
        return Results.NoContent();
    }

    private static JobStatusResponse ToStatus(JobRecord job) => new(
        job.JobId,
        job.Status.ToString().ToLowerInvariant(),
        job.Progress,
        job.Stage,
        job.CreatedAt,
        job.UpdatedAt,
        job.ExpiresAt,
        job.OriginalFileName,
        job.FileSize,
        job.SplitMethod?.ToString().ToLowerInvariant(),
        job.PublicErrorCode,
        job.PublicErrorMessage,
        job.Status == JobStatus.Completed,
        job.ResultSize);

    private static async Task ReplaceJobAsync(
        IJobStore store, JobRecord expected, JobRecord updated, CancellationToken cancellationToken)
    {
        if (updated.ExpiresAt <= updated.UpdatedAt ||
            !await store.TryReplaceAsync(expected, updated, cancellationToken))
        {
            throw new PilahDokException("job_changed", "Job telah berubah, dihapus, atau kedaluwarsa. Muat ulang status sebelum mencoba lagi.", 409);
        }
    }

    private static string DecodeFileName(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            throw new PilahDokException("filename_required", "Nama file wajib dikirim.", 400);
        }

        try
        {
            return Uri.UnescapeDataString(encoded);
        }
        catch (UriFormatException exception)
        {
            throw new PilahDokException("invalid_filename", "Nama file tidak valid.", 400, exception);
        }
    }

    private static void ValidateUploadHeaders(HttpRequest request, string fileName, long maxBytes)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".docm", StringComparison.OrdinalIgnoreCase))
        {
            throw new PilahDokException("macro_not_supported", "File .docm dengan macro belum didukung.", 415);
        }

        if (!extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            throw new PilahDokException("unsupported_file_type", "PilahDok hanya menerima file .docx.", 415);
        }

        if (request.ContentLength is > 0 && request.ContentLength > maxBytes)
        {
            throw new PilahDokException("file_too_large", "Ukuran file melebihi batas yang diizinkan.", 413);
        }

        var allowedContentTypes = new[]
        {
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/octet-stream"
        };
        if (!string.IsNullOrWhiteSpace(request.ContentType) &&
            !allowedContentTypes.Contains(request.ContentType.Split(';')[0], StringComparer.OrdinalIgnoreCase))
        {
            throw new PilahDokException("unsupported_content_type", "Content-Type upload tidak didukung.", 415);
        }
    }

    private static async Task<long> StreamUploadAsync(
        Stream requestBody,
        string destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await requestBody.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maximumBytes)
            {
                throw new PilahDokException("file_too_large", "Ukuran file melebihi batas yang diizinkan.", 413);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        return total;
    }
}
