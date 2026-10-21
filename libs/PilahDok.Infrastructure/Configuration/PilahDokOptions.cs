using System.ComponentModel.DataAnnotations;

namespace PilahDok.Infrastructure.Configuration;

public sealed class PilahDokOptions
{
    public const string SectionName = "PilahDok";

    [Range(1_024, 1_073_741_824)]
    public long MaxUploadBytes { get; init; } = 52_428_800;

    [Range(1_024, 4_294_967_296)]
    public long MaxOutputBytes { get; init; } = 536_870_912;

    [Range(10, 10_000)]
    public int MaxZipEntries { get; init; } = 2_048;

    [Range(1_024, 4_294_967_296)]
    public long MaxUncompressedBytes { get; init; } = 209_715_200;

    [Range(1, 10_000)]
    public double MaxCompressionRatio { get; init; } = 100;

    [Range(1, 1_440)]
    public int JobTtlMinutes { get; init; } = 60;

    [Range(1, 60)]
    public int DownloadGraceMinutes { get; init; } = 10;

    [Range(10, 3_600)]
    public int ProcessingTimeoutSeconds { get; init; } = 300;

    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(10, 3_600)]
    public int UploadTimeoutSeconds { get; init; } = 120;

    [Range(5, 3_600)]
    public int CleanupIntervalSeconds { get; init; } = 60;

    [Range(1, 10_000)]
    public int RateLimitPerMinute { get; init; } = 30;

    [Required]
    public string StorageRoot { get; init; } = "/tmp/pilahdok/jobs";

    [Required]
    public string RedisConnection { get; init; } = "redis:6379,abortConnect=false";

    public string[] AllowedOrigins { get; init; } = ["http://localhost:3000"];

    public string AntivirusMode { get; init; } = "disabled";
    public string ClamAvHost { get; init; } = "clamav";

    [Range(1, 65_535)]
    public int ClamAvPort { get; init; } = 3310;

    public bool VisualPaginationEnabled { get; init; }
}
