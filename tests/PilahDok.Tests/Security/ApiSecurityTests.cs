using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PilahDok.Api.Contracts;
using PilahDok.Core.Abstractions;
using PilahDok.Testing;
using PilahDok.Tests.Support;

namespace PilahDok.Tests.Security;

public sealed class ApiSecurityTests
{
    [Fact]
    public async Task JobCannotBeAccessedWithoutCapabilityToken()
    {
        using var factory = new PilahDokWebFactory();
        using var client = factory.CreateClient();
        var secrets = await CreateJobAsync(client);
        var response = await client.GetAsync($"/api/v1/jobs/{secrets.JobId}", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FakeDocxIsRejectedWithoutLeakingStackTrace()
    {
        using var factory = new PilahDokWebFactory();
        using var client = factory.CreateClient();
        var secrets = await CreateJobAsync(client);
        var response = await UploadAsync(client, secrets, "fake.docx", "not a zip"u8.ToArray());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.Contains("invalid_docx", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedUploadIsRejected()
    {
        using var factory = new PilahDokWebFactory(maxUploadBytes: 1_024);
        using var client = factory.CreateClient();
        var secrets = await CreateJobAsync(client);
        var response = await UploadAsync(client, secrets, "large.docx", new byte[2_048]);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task TraversalFilenameIsSanitizedAndNeverUsedAsAPath()
    {
        using var factory = new PilahDokWebFactory();
        using var client = factory.CreateClient();
        var fixturePath = Path.Combine(factory.StorageRoot, "safe-fixture.docx");
        FixtureDocumentBuilder.Create(fixturePath);
        var secrets = await CreateJobAsync(client);
        var response = await UploadAsync(
            client,
            secrets,
            "../../outside.docx",
            await File.ReadAllBytesAsync(fixturePath, CancellationToken.None));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JobStatusResponse>(cancellationToken: CancellationToken.None);
        Assert.Equal("outside.docx", status?.OriginalFileName);
        Assert.False(File.Exists(Path.Combine(factory.StorageRoot, "outside.docx")));
    }

    [Fact]
    public async Task ExpiredJobIsDeletedAndReturnsGone()
    {
        using var factory = new PilahDokWebFactory();
        using var client = factory.CreateClient();
        var secrets = await CreateJobAsync(client);
        factory.Clock.Advance(TimeSpan.FromMinutes(61));
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}");
        request.Headers.Add("X-Job-Token", secrets.AccessToken);
        var response = await client.SendAsync(request, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Null(await factory.Store.GetAsync(secrets.JobId, CancellationToken.None));
    }

    [Fact]
    public async Task RateLimiterRejectsBurstBeyondConfiguredLimit()
    {
        using var factory = new PilahDokWebFactory(rateLimit: 3);
        using var client = factory.CreateClient();
        var responses = new List<HttpResponseMessage>();
        for (var index = 0; index < 31; index++)
        {
            responses.Add(await client.PostAsync("/api/v1/jobs", null, CancellationToken.None));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, responses[^1].StatusCode);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static async Task<CreateJobResponse> CreateJobAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/jobs", null, CancellationToken.None);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CreateJobResponse>(cancellationToken: CancellationToken.None) ??
               throw new InvalidDataException("Create job response was empty.");
    }

    [Fact]
    public async Task DeleteDuringValidationDoesNotResurrectMetadataOrFiles()
    {
        var scanner = new BlockingScanner();
        using var factory = new PilahDokWebFactory(scanner: scanner);
        using var client = factory.CreateClient();
        var fixture = Path.Combine(factory.StorageRoot, "delete-race.docx");
        FixtureDocumentBuilder.Create(fixture);
        var secrets = await CreateJobAsync(client);
        var upload = UploadAsync(client, secrets, "safe.docx", await File.ReadAllBytesAsync(fixture, CancellationToken.None));
        await scanner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/jobs/{secrets.JobId}");
        delete.Headers.Add("X-Job-Token", secrets.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete, CancellationToken.None)).StatusCode);
        scanner.Resume.SetResult();
        Assert.Equal(HttpStatusCode.Conflict, (await upload).StatusCode);
        Assert.Null(await factory.Store.GetAsync(secrets.JobId, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Combine(factory.StorageRoot, secrets.JobId)));
    }

    [Fact]
    public async Task PollingDoesNotConsumeMutationRateLimit()
    {
        using var factory = new PilahDokWebFactory(rateLimit: 3);
        using var client = factory.CreateClient();
        var secrets = await CreateJobAsync(client);
        for (var index = 0; index < 35; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}");
            request.Headers.Add("X-Job-Token", secrets.AccessToken);
            using var response = await client.SendAsync(request, CancellationToken.None);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var created = await client.PostAsync("/api/v1/jobs", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    private sealed class BlockingScanner : IAntivirusScanner
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ScanAsync(string path, CancellationToken cancellationToken)
        {
            Entered.SetResult();
            await Resume.Task.WaitAsync(cancellationToken);
        }
    }

    private static Task<HttpResponseMessage> UploadAsync(
        HttpClient client,
        CreateJobResponse secrets,
        string filename,
        byte[] bytes)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/jobs/{secrets.JobId}/document")
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Headers.Add("X-Job-Token", secrets.AccessToken);
        request.Headers.Add("X-File-Name", Uri.EscapeDataString(filename));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        return client.SendAsync(request, CancellationToken.None);
    }
}
