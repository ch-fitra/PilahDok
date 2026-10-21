using System.Collections.Concurrent;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;

namespace PilahDok.Tests.Support;

public sealed class FakeClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initial;
    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);
}

public sealed class InMemoryJobStore : IJobStore
{
    private readonly ConcurrentDictionary<string, JobRecord> _jobs = new(StringComparer.Ordinal);

    public Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.TryGetValue(jobId, out var job);
        return Task.FromResult(job);
    }

    public Task SetAsync(JobRecord job, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs[job.JobId] = job;
        return Task.CompletedTask;
    }

    public Task<bool> TryReplaceAsync(
        JobRecord expected,
        JobRecord updated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_jobs.TryUpdate(expected.JobId, updated, expected));
    }

    public Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _jobs.TryRemove(jobId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetExpiredJobIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = _jobs.Values
            .Where(job => job.ExpiresAt <= now)
            .OrderBy(job => job.ExpiresAt)
            .Take(limit)
            .Select(job => job.JobId)
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }
}

public sealed class InMemoryJobBroker : IJobBroker, IDisposable
{
    private readonly ConcurrentQueue<JobQueueMessage> _messages = new();
    private readonly SemaphoreSlim _available = new(0);

    public Task EnqueueAsync(JobQueueMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _messages.Enqueue(message);
        _available.Release();
        return Task.CompletedTask;
    }

    public async Task<JobQueueMessage?> DequeueAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        if (!await _available.WaitAsync(wait, cancellationToken))
        {
            return null;
        }

        return _messages.TryDequeue(out var message) ? message : null;
    }

    public void Dispose() => _available.Dispose();
}
