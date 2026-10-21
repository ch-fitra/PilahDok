using PilahDok.Core.Errors;
using PilahDok.Core.Models;

namespace PilahDok.Core.Services;

public static class JobStateMachine
{
    private static readonly Dictionary<JobStatus, HashSet<JobStatus>> Allowed =
        new Dictionary<JobStatus, HashSet<JobStatus>>
        {
            [JobStatus.Created] = [JobStatus.Uploading, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Uploading] = [JobStatus.Validating, JobStatus.Failed, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Validating] = [JobStatus.Uploaded, JobStatus.Failed, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Uploaded] = [JobStatus.Queued, JobStatus.Uploading, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Queued] = [JobStatus.Analyzing, JobStatus.Processing, JobStatus.Failed, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Analyzing] = [JobStatus.Ready, JobStatus.Failed, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Ready] = [JobStatus.Queued, JobStatus.Uploading, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Processing] = [JobStatus.Completed, JobStatus.Failed, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Completed] = [JobStatus.Queued, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Failed] = [JobStatus.Queued, JobStatus.Uploading, JobStatus.Deleted, JobStatus.Expired],
            [JobStatus.Expired] = [JobStatus.Deleted],
            [JobStatus.Deleted] = []
        };

    public static JobRecord Transition(
        JobRecord job,
        JobStatus next,
        DateTimeOffset now,
        int progress,
        string stage)
    {
        if (job.Status != next && (!Allowed.TryGetValue(job.Status, out var targets) || !targets.Contains(next)))
        {
            throw new InvalidJobStateException(job.Status, next);
        }

        return job with
        {
            Status = next,
            UpdatedAt = now,
            Progress = Math.Clamp(progress, 0, 100),
            Stage = stage,
            PublicErrorCode = null,
            PublicErrorMessage = null
        };
    }

    public static void Ensure(JobRecord job, params JobStatus[] allowed)
    {
        if (!allowed.Contains(job.Status))
        {
            throw new InvalidJobStateException(job.Status, allowed);
        }
    }
}
