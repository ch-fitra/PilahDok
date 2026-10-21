using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;
using PilahDok.Core.Services;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.DocumentProcessing;
using PilahDok.Infrastructure.Jobs;
using PilahDok.Infrastructure.Observability;
using PilahDok.Infrastructure.Storage;
using PilahDok.Testing;
using PilahDok.Tests.Support;

namespace PilahDok.Tests.Integration;

public sealed class WorkerRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pilahdok-worker-test", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ProcessorContinuesAfterControlledFailure()
    {
        var store = new InMemoryJobStore();
        var files = new LocalJobFileStore(Options.Create(new PilahDokOptions
        {
            StorageRoot = _directory,
            RedisConnection = "localhost"
        }));
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero));
        var engine = new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance);
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var processor = new JobOperationProcessor(
            store,
            files,
            engine,
            clock,
            new PilahDokMetrics(services.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>()),
            NullLogger<JobOperationProcessor>.Instance);

        var failedJob = CreateQueuedJob(new string('e', 32), clock.UtcNow, (JobOperation)999);
        await store.SetAsync(failedJob, CancellationToken.None);
        await processor.ProcessAsync(new JobQueueMessage(failedJob.JobId, (JobOperation)999), CancellationToken.None);
        Assert.Equal(JobStatus.Failed, (await store.GetAsync(failedJob.JobId, CancellationToken.None))?.Status);

        var validJob = CreateQueuedJob(new string('f', 32), clock.UtcNow, JobOperation.Analyze);
        await files.EnsureJobDirectoryAsync(validJob.JobId, CancellationToken.None);
        FixtureDocumentBuilder.Create(files.GetInputPath(validJob.JobId));
        await store.SetAsync(validJob, CancellationToken.None);
        await processor.ProcessAsync(new JobQueueMessage(validJob.JobId, JobOperation.Analyze), CancellationToken.None);
        Assert.Equal(JobStatus.Ready, (await store.GetAsync(validJob.JobId, CancellationToken.None))?.Status);
        // A duplicate delivery after completion must not turn a successful analysis into failed.
        await processor.ProcessAsync(new JobQueueMessage(validJob.JobId, JobOperation.Analyze), CancellationToken.None);
        Assert.Equal(JobStatus.Ready, (await store.GetAsync(validJob.JobId, CancellationToken.None))?.Status);
    }

    private static JobRecord CreateQueuedJob(string id, DateTimeOffset now, JobOperation operation) => new()
    {
        JobId = id,
        AccessTokenHash = SecureToken.Hash("access"),
        DownloadTokenHash = SecureToken.Hash("download"),
        Status = JobStatus.Queued,
        CreatedAt = now,
        UpdatedAt = now,
        ExpiresAt = now.AddHours(1),
        PendingOperation = operation,
        InternalCorrelationId = "worker-recovery"
    };

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
