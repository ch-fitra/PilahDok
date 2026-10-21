using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Errors;
using PilahDok.Infrastructure.Configuration;

namespace PilahDok.Infrastructure.Security;

public sealed class NoopAntivirusScanner : IAntivirusScanner
{
    public Task ScanAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed partial class ClamAvAntivirusScanner(
    IOptions<PilahDokOptions> options,
    ILogger<ClamAvAntivirusScanner> logger) : IAntivirusScanner
{
    private readonly PilahDokOptions _options = options.Value;

    public async Task ScanAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(_options.ClamAvHost, _options.ClamAvPort, cancellationToken);
            await using var network = client.GetStream();
            await network.WriteAsync(Encoding.ASCII.GetBytes("zINSTREAM\0"), cancellationToken);

            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var buffer = new byte[64 * 1024];
            var lengthBuffer = new byte[4];
            int read;
            while ((read = await file.ReadAsync(buffer, cancellationToken)) > 0)
            {
                BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, read);
                await network.WriteAsync(lengthBuffer, cancellationToken);
                await network.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            BinaryPrimitives.WriteInt32BigEndian(lengthBuffer, 0);
            await network.WriteAsync(lengthBuffer, cancellationToken);
            await network.FlushAsync(cancellationToken);

            var responseBuffer = new byte[1_024];
            var responseLength = await network.ReadAsync(responseBuffer, cancellationToken);
            var response = Encoding.ASCII.GetString(responseBuffer, 0, responseLength);
            if (response.Contains("FOUND", StringComparison.OrdinalIgnoreCase))
            {
                throw new PilahDokException("malware_detected", "File ditolak oleh pemindai keamanan.", 422);
            }

            if (!response.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("ClamAV returned an unexpected response.");
            }
        }
        catch (PilahDokException)
        {
            throw;
        }
        catch (Exception exception) when (_options.AntivirusMode.Equals("fail-open", StringComparison.OrdinalIgnoreCase))
        {
            LogScannerUnavailable(logger, exception);
        }
        catch (Exception exception)
        {
            throw new PilahDokException(
                "antivirus_unavailable",
                "Pemindai keamanan sedang tidak tersedia. Silakan coba lagi nanti.",
                503,
                exception);
        }
    }

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Antivirus scanner unavailable; continuing because fail-open mode is configured")]
    private static partial void LogScannerUnavailable(ILogger logger, Exception exception);
}
