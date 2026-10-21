using System.Text.Json;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;
using PilahDok.Infrastructure.Serialization;
using StackExchange.Redis;

namespace PilahDok.Infrastructure.Jobs;

public sealed class RedisJobStore(IConnectionMultiplexer connection) : IJobStore
{
    private const string ExpiryIndex = "pilahdok:jobs:expiry";
    private readonly IDatabase _database = connection.GetDatabase();

    public async Task<JobRecord?> GetAsync(string jobId, CancellationToken cancellationToken)
    {
        var value = await _database.StringGetAsync(JobKey(jobId)).WaitAsync(cancellationToken);
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<JobRecord>(value.ToString(), JsonDefaults.Options);
    }

    public async Task SetAsync(JobRecord job, CancellationToken cancellationToken)
    {
        var retention = job.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        if (retention < TimeSpan.FromMinutes(10))
        {
            retention = TimeSpan.FromMinutes(10);
        }

        var json = JsonSerializer.Serialize(job, JsonDefaults.Options);
        var transaction = _database.CreateTransaction();
        _ = transaction.StringSetAsync(JobKey(job.JobId), json, retention);
        _ = transaction.SortedSetAddAsync(ExpiryIndex, job.JobId, job.ExpiresAt.ToUnixTimeSeconds());
        var committed = await transaction.ExecuteAsync().WaitAsync(cancellationToken);
        if (!committed)
        {
            throw new InvalidOperationException("Redis transaction for job metadata was not committed.");
        }
    }

    public async Task<bool> TryReplaceAsync(
        JobRecord expected,
        JobRecord updated,
        CancellationToken cancellationToken)
    {
        var retention = updated.ExpiresAt - DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);
        if (retention < TimeSpan.FromMinutes(10))
        {
            retention = TimeSpan.FromMinutes(10);
        }

        var key = JobKey(expected.JobId);
        var transaction = _database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(key, JsonSerializer.Serialize(expected, JsonDefaults.Options)));
        _ = transaction.StringSetAsync(key, JsonSerializer.Serialize(updated, JsonDefaults.Options), retention);
        _ = transaction.SortedSetAddAsync(ExpiryIndex, updated.JobId, updated.ExpiresAt.ToUnixTimeSeconds());
        return await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    public async Task DeleteAsync(string jobId, CancellationToken cancellationToken)
    {
        var transaction = _database.CreateTransaction();
        _ = transaction.KeyDeleteAsync(JobKey(jobId));
        _ = transaction.SortedSetRemoveAsync(ExpiryIndex, jobId);
        await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetExpiredJobIdsAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken)
    {
        var values = await _database.SortedSetRangeByScoreAsync(
                ExpiryIndex,
                stop: now.ToUnixTimeSeconds(),
                take: limit)
            .WaitAsync(cancellationToken);
        return values.Select(static value => value.ToString()).Where(static value => value.Length > 0).ToArray();
    }

    private static string JobKey(string jobId) => $"pilahdok:job:{jobId}";
}
