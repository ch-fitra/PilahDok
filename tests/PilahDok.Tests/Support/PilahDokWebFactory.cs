using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PilahDok.Core.Abstractions;
using PilahDok.Infrastructure.Security;

namespace PilahDok.Tests.Support;

public sealed class PilahDokWebFactory(int rateLimit = 1_000, long maxUploadBytes = 52_428_800, IAntivirusScanner? scanner = null)
    : WebApplicationFactory<Program>
{
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "pilahdok-tests", Guid.NewGuid().ToString("N"));
    public FakeClock Clock { get; } = new(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero));
    public InMemoryJobStore Store { get; } = new();
    public InMemoryJobBroker Broker { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PilahDok:StorageRoot"] = StorageRoot,
                ["PilahDok:RedisConnection"] = "localhost:1,abortConnect=false,connectTimeout=50,syncTimeout=50",
                ["PilahDok:RateLimitPerMinute"] = rateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["PilahDok:MaxUploadBytes"] = maxUploadBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["PilahDok:JobTtlMinutes"] = "60",
                ["PilahDok:DownloadGraceMinutes"] = "10",
                ["PilahDok:AntivirusMode"] = "disabled"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.RemoveAll<IJobStore>();
            services.RemoveAll<IJobBroker>();
            services.RemoveAll<IAntivirusScanner>();
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<IJobStore>(Store);
            services.AddSingleton<IJobBroker>(Broker);
            services.AddSingleton<IAntivirusScanner>(scanner ?? new NoopAntivirusScanner());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(StorageRoot))
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
    }
}
