using PilahDok.Core.Abstractions;
using PilahDok.Core.Errors;
using PilahDok.Core.Models;
using PilahDok.Core.Services;

namespace PilahDok.Api.Security;

public sealed class JobAuthorization(IJobStore jobStore, IJobFileStore fileStore, IClock clock)
{
    public async Task<JobRecord> RequireAccessAsync(
        string jobId,
        string? suppliedToken,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(jobId, cancellationToken);
        if (job is null || string.IsNullOrWhiteSpace(suppliedToken) || !SecureToken.Matches(suppliedToken, job.AccessTokenHash))
        {
            throw new PilahDokException("job_not_found", "Job tidak ditemukan.", 404);
        }

        await EnsureNotExpiredAsync(job, cancellationToken);
        return job;
    }

    public async Task<JobRecord> RequireDownloadAsync(
        string jobId,
        string? suppliedToken,
        CancellationToken cancellationToken)
    {
        var job = await jobStore.GetAsync(jobId, cancellationToken);
        if (job is null || string.IsNullOrWhiteSpace(suppliedToken) || !SecureToken.Matches(suppliedToken, job.DownloadTokenHash))
        {
            throw new PilahDokException("job_not_found", "Job tidak ditemukan.", 404);
        }

        await EnsureNotExpiredAsync(job, cancellationToken);
        return job;
    }

    private async Task EnsureNotExpiredAsync(JobRecord job, CancellationToken cancellationToken)
    {
        if (job.ExpiresAt > clock.UtcNow)
        {
            return;
        }

        await fileStore.DeleteJobAsync(job.JobId, cancellationToken);
        await jobStore.DeleteAsync(job.JobId, cancellationToken);
        throw new PilahDokException("job_expired", "Job telah kedaluwarsa dan seluruh file sudah dihapus.", 410);
    }
}
