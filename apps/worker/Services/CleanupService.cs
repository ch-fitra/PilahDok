using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.Observability;

namespace PilahDok.Worker.Services;

public sealed class CleanupService(
    IJobCleanupService cleanup,
    IOptions<PilahDokOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunOnceAsync(stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await cleanup.RunOnceAsync(cancellationToken);
    }
}
