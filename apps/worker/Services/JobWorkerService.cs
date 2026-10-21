using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Infrastructure.Configuration;

namespace PilahDok.Worker.Services;

public sealed partial class JobWorkerService(
    IJobBroker queue,
    IJobOperationProcessor processor,
    IOptions<PilahDokOptions> options,
    ILogger<JobWorkerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogWorkerStarted(logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var message = await queue.DequeueAsync(TimeSpan.FromSeconds(2), stoppingToken);
                if (message is null)
                {
                    continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ProcessingTimeoutSeconds));
                await processor.ProcessAsync(message, timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                LogProcessingTimeout(logger);
            }
            catch (Exception exception)
            {
                LogWorkerRecovered(logger, exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "PilahDok job worker started")]
    private static partial void LogWorkerStarted(ILogger logger);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "A document operation exceeded the configured processing timeout")]
    private static partial void LogProcessingTimeout(ILogger logger);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Worker loop recovered after {ExceptionType}")]
    private static partial void LogWorkerRecovered(ILogger logger, string exceptionType);
}
