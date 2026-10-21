using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.Jobs;
using PilahDok.Infrastructure.Observability;
using PilahDok.Infrastructure.Storage;
using PilahDok.Tests.Support;

namespace PilahDok.Tests.Unit;

public sealed class CleanupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pilahdok-cleanup", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CleanupDeletesExpiredMetadataAndArtifacts()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero));
        var store = new InMemoryJobStore();
        var options = Options.Create(new PilahDokOptions
        {
            StorageRoot = _directory,
            RedisConnection = "localhost",
            JobTtlMinutes = 60
        });
        var files = new LocalJobFileStore(options);
        var job = CreateExpiredJob(clock.UtcNow.AddMinutes(-1));
        await files.EnsureJobDirectoryAsync(job.JobId, CancellationToken.None);
        await File.WriteAllTextAsync(files.GetInputPath(job.JobId), "temporary", CancellationToken.None);
        await store.SetAsync(job, CancellationToken.None);

        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var metrics = new PilahDokMetrics(services.GetRequiredService<IMeterFactory>());
        var cleanup = new JobCleanupService(
            store,
            files,
            new InMemoryJobBroker(),
            clock,
            options,
            metrics,
            NullLogger<JobCleanupService>.Instance);
        await cleanup.RunOnceAsync(CancellationToken.None);

        Assert.Null(await store.GetAsync(job.JobId, CancellationToken.None));
        Assert.False(File.Exists(files.GetInputPath(job.JobId)));
    }

    [Fact]
    public async Task CleanupHonorsOriginalExpiryAfterRedisMetadataIsLost()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new InMemoryJobStore();
        var options = Options.Create(new PilahDokOptions { StorageRoot = _directory });
        var files = new LocalJobFileStore(options);
        var id = new string('a', 32);
        await files.EnsureJobDirectoryAsync(id, CancellationToken.None);
        await files.SetExpiryAsync(id, clock.UtcNow.AddSeconds(10), CancellationToken.None);
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var cleanup = new JobCleanupService(store, files, new InMemoryJobBroker(), clock, options,
            new PilahDokMetrics(services.GetRequiredService<IMeterFactory>()), NullLogger<JobCleanupService>.Instance);
        await cleanup.RunOnceAsync(CancellationToken.None);
        Assert.True(Directory.Exists(Path.GetDirectoryName(files.GetAnalysisPath(id))));
        clock.Advance(TimeSpan.FromSeconds(11));
        await cleanup.RunOnceAsync(CancellationToken.None);
        Assert.False(Directory.Exists(Path.GetDirectoryName(files.GetAnalysisPath(id))));
    }

    [Fact]
    public async Task RecoveryRequeuesLostQueuedMessageAndFailsCrashedProcessing()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new InMemoryJobStore();
        using var broker = new InMemoryJobBroker();
        var options = Options.Create(new PilahDokOptions { StorageRoot = _directory });
        var files = new LocalJobFileStore(options);
        var queued = CreateExpiredJob(clock.UtcNow.AddHours(1)) with
        {
            Status = JobStatus.Queued,
            UpdatedAt = clock.UtcNow.AddMinutes(-10),
            PendingOperation = JobOperation.Analyze
        };
        var crashed = queued with { JobId = new string('b', 32), Status = JobStatus.Processing };
        foreach (var job in new[] { queued, crashed })
        {
            await files.EnsureJobDirectoryAsync(job.JobId, CancellationToken.None);
            await store.SetAsync(job, CancellationToken.None);
        }
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var cleanup = new JobCleanupService(store, files, broker, clock, options,
            new PilahDokMetrics(services.GetRequiredService<IMeterFactory>()), NullLogger<JobCleanupService>.Instance);
        await cleanup.RunOnceAsync(CancellationToken.None);
        Assert.Equal(queued.JobId, (await broker.DequeueAsync(TimeSpan.FromSeconds(1), CancellationToken.None))?.JobId);
        Assert.Equal(JobStatus.Failed, (await store.GetAsync(crashed.JobId, CancellationToken.None))?.Status);
        Assert.Equal("operation_interrupted", (await store.GetAsync(crashed.JobId, CancellationToken.None))?.PublicErrorCode);
    }

    private static JobRecord CreateExpiredJob(DateTimeOffset expiresAt) => new()
    {
        JobId = new string('d', 32),
        AccessTokenHash = new string('a', 64),
        DownloadTokenHash = new string('b', 64),
        Status = JobStatus.Ready,
        CreatedAt = expiresAt.AddHours(-1),
        UpdatedAt = expiresAt,
        ExpiresAt = expiresAt,
        InternalCorrelationId = "cleanup-test"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
