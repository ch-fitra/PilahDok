using System.Text.Json;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;
using PilahDok.Infrastructure.Serialization;
using StackExchange.Redis;

namespace PilahDok.Infrastructure.Jobs;

public sealed class RedisJobBroker(IConnectionMultiplexer connection) : IJobBroker
{
    private const string QueueKey = "pilahdok:jobs:queue";
    private readonly IDatabase _database = connection.GetDatabase();

    public async Task EnqueueAsync(JobQueueMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonDefaults.Options);
        await _database.ListLeftPushAsync(QueueKey, json).WaitAsync(cancellationToken);
    }

    public async Task<JobQueueMessage?> DequeueAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        do
        {
            var value = await _database.ListRightPopAsync(QueueKey).WaitAsync(cancellationToken);
            if (!value.IsNullOrEmpty)
            {
                return JsonSerializer.Deserialize<JobQueueMessage>(value.ToString(), JsonDefaults.Options);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return null;
    }
}
