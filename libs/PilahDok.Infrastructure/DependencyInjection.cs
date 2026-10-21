using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Services;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.DocumentProcessing;
using PilahDok.Infrastructure.Jobs;
using PilahDok.Infrastructure.Security;
using PilahDok.Infrastructure.Storage;
using StackExchange.Redis;

namespace PilahDok.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPilahDokInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PilahDokOptions>()
            .Bind(configuration.GetSection(PilahDokOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.AntivirusMode is "disabled" or "fail-open" or "fail-closed",
                "PilahDok:AntivirusMode must be disabled, fail-open, or fail-closed.")
            .ValidateOnStart();

        services.AddSingleton<IConnectionMultiplexer>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PilahDokOptions>>().Value;
            return ConnectionMultiplexer.Connect(options.RedisConnection);
        });
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IJobStore, RedisJobStore>();
        services.AddSingleton<IJobBroker, RedisJobBroker>();
        services.AddSingleton<IJobFileStore, LocalJobFileStore>();
        services.AddSingleton<IDocxPackageValidator, DocxPackageValidator>();
        services.AddSingleton<IDocumentEngine, OpenXmlDocumentEngine>();
        services.AddSingleton<IWordPaginationEngine, UnavailablePaginationEngine>();
        services.AddSingleton<IJobOperationProcessor, JobOperationProcessor>();
        services.AddSingleton<IJobCleanupService, JobCleanupService>();
        services.AddSingleton<IAntivirusScanner>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<PilahDokOptions>>().Value;
            return options.AntivirusMode.Equals("disabled", StringComparison.OrdinalIgnoreCase)
                ? new NoopAntivirusScanner()
                : ActivatorUtilities.CreateInstance<ClamAvAntivirusScanner>(provider);
        });
        return services;
    }
}
