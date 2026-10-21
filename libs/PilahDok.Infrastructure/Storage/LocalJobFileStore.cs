using System.Text.RegularExpressions;
using System.Globalization;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Infrastructure.Configuration;

namespace PilahDok.Infrastructure.Storage;

public sealed partial class LocalJobFileStore : IJobFileStore
{
    private readonly string _root;

    public LocalJobFileStore(IOptions<PilahDokOptions> options)
    {
        _root = Path.GetFullPath(options.Value.StorageRoot);
        Directory.CreateDirectory(_root);
    }

    public string GetInputPath(string jobId) => Path.Combine(GetJobDirectory(jobId), "input", "source.docx");
    public string GetAnalysisPath(string jobId) => Path.Combine(GetJobDirectory(jobId), "analysis.json");
    public string GetOutputDirectory(string jobId) => Path.Combine(GetJobDirectory(jobId), "output");
    public string GetResultZipPath(string jobId) => Path.Combine(GetJobDirectory(jobId), "result.zip");

    public Task EnsureJobDirectoryAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(GetInputPath(jobId))!);
        Directory.CreateDirectory(GetOutputDirectory(jobId));
        return Task.CompletedTask;
    }

    public Task DeleteJobAsync(string jobId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = GetJobDirectory(jobId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task SetExpiryAsync(string jobId, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(Path.Combine(GetJobDirectory(jobId), "expires-at"),
            expiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), cancellationToken);

    public async Task<IReadOnlyList<string>> FindExpiredJobIdsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var expired = new List<string>();
        foreach (var path in Directory.EnumerateDirectories(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(path);
            if (!JobIdPattern().IsMatch(id)) continue;
            var marker = Path.Combine(path, "expires-at");
            try
            {
                var text = await File.ReadAllTextAsync(marker, cancellationToken);
                if (long.TryParse(text, CultureInfo.InvariantCulture, out var timestamp) && timestamp <= now.ToUnixTimeMilliseconds())
                    expired.Add(id);
            }
            catch (FileNotFoundException) { /* Older jobs use orphan-age cleanup. */ }
            catch (DirectoryNotFoundException) { /* Concurrent delete already removed this job. */ }
        }
        return expired;
    }

    public Task<IReadOnlyList<string>> FindOrphanedJobIdsAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root))
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        var ids = Directory.EnumerateDirectories(_root)
            .Where(path => Directory.GetLastWriteTimeUtc(path) < olderThan.UtcDateTime)
            .Select(Path.GetFileName)
            .Where(id => id is not null && JobIdPattern().IsMatch(id))
            .Cast<string>()
            .ToArray();
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    private string GetJobDirectory(string jobId)
    {
        if (!JobIdPattern().IsMatch(jobId))
        {
            throw new ArgumentException("Invalid job identifier.", nameof(jobId));
        }

        var path = Path.GetFullPath(Path.Combine(_root, jobId));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Job path escaped the configured storage root.");
        }

        return path;
    }

    [GeneratedRegex("^[a-f0-9]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex JobIdPattern();
}
