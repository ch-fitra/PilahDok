using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.DependencyInjection;
using PilahDok.Api.Contracts;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Models;
using PilahDok.Testing;
using PilahDok.Tests.Support;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PilahDok.Tests.Integration;

public sealed class ApiWorkflowTests : IDisposable
{
    private readonly PilahDokWebFactory _factory = new();
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    public ApiWorkflowTests()
    {
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task FullWorkflowUploadAnalyzeSplitDownloadAndDelete()
    {
        var fixturePath = Path.Combine(_factory.StorageRoot, "source-fixture.docx");
        FixtureDocumentBuilder.Create(fixturePath);
        var fixture = await File.ReadAllBytesAsync(fixturePath, CancellationToken.None);
        var secrets = await CreateJobAsync();

        var upload = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/jobs/{secrets.JobId}/document")
        {
            Content = new ByteArrayContent(fixture)
        };
        upload.Headers.Add("X-Job-Token", secrets.AccessToken);
        upload.Headers.Add("X-File-Name", Uri.EscapeDataString("skripsi-fixture.docx"));
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        var uploadResponse = await _client.SendAsync(upload, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, uploadResponse.StatusCode);

        var analyzeResponse = await SendWithAccessAsync(HttpMethod.Post, $"/api/v1/jobs/{secrets.JobId}/analyze", secrets.AccessToken);
        Assert.Equal(HttpStatusCode.Accepted, analyzeResponse.StatusCode);
        await ProcessNextAsync();

        var analysisResponse = await SendWithAccessAsync(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}/analysis", secrets.AccessToken);
        Assert.Equal(HttpStatusCode.OK, analysisResponse.StatusCode);
        var analysis = await analysisResponse.Content.ReadFromJsonAsync<AnalysisResult>(Json, CancellationToken.None);
        Assert.NotNull(analysis);
        var headings = analysis.SplitCandidates
            .Where(candidate => candidate.Type == SplitCandidateType.Heading1)
            .OrderBy(candidate => candidate.StructureIndex)
            .ToArray();
        Assert.Equal(3, headings.Length);

        var segments = new List<object>
        {
            new { startCandidateId = (string?)null, outputFileName = "01-Bagian-Awal.docx" }
        };
        segments.AddRange(headings.Select((candidate, index) => (object)new
        {
            startCandidateId = candidate.Id,
            outputFileName = $"{index + 2:00}-{candidate.Title}.docx"
        }));
        var split = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/jobs/{secrets.JobId}/split")
        {
            Content = JsonContent.Create(new { method = "heading1", segments })
        };
        split.Headers.Add("X-Job-Token", secrets.AccessToken);
        var splitResponse = await _client.SendAsync(split, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Accepted, splitResponse.StatusCode);
        await ProcessNextAsync();

        var statusResponse = await SendWithAccessAsync(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}", secrets.AccessToken);
        var status = await statusResponse.Content.ReadFromJsonAsync<JobStatusResponse>(Json, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("completed", status.Status);
        Assert.True(status.DownloadAvailable);

        var wrongDownload = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}/download");
        wrongDownload.Headers.Add("X-Download-Token", "wrong-token");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(wrongDownload, CancellationToken.None)).StatusCode);

        var download = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}/download");
        download.Headers.Add("X-Download-Token", secrets.DownloadToken);
        var downloadResponse = await _client.SendAsync(download, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        Assert.Equal("application/zip", downloadResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", downloadResponse.Content.Headers.ContentDisposition?.DispositionType, StringComparison.OrdinalIgnoreCase);
        var zipBytes = await downloadResponse.Content.ReadAsByteArrayAsync(CancellationToken.None);
        ValidateResultZip(zipBytes, expectedDocumentCount: 4);

        var deleteResponse = await SendWithAccessAsync(HttpMethod.Delete, $"/api/v1/jobs/{secrets.JobId}", secrets.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        var afterDelete = await SendWithAccessAsync(HttpMethod.Get, $"/api/v1/jobs/{secrets.JobId}", secrets.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);
    }

    private static void ValidateResultZip(byte[] bytes, int expectedDocumentCount)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Contains(archive.Entries, entry => entry.FullName == "manifest.json");
        var documents = archive.Entries.Where(entry => entry.FullName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(expectedDocumentCount, documents.Length);

        var foundImage = false;
        var foundNumbering = false;
        var foundTable = false;
        var foundHyperlink = false;
        foreach (var entry in documents)
        {
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            buffer.Position = 0;
            using var document = WordprocessingDocument.Open(buffer, false);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2013).Validate(document).ToArray();
            Assert.Empty(errors);
            var main = document.MainDocumentPart ?? throw new InvalidDataException("Output DOCX has no main part.");
            var body = main.Document?.Body ?? throw new InvalidDataException("Output DOCX has no body.");
            foundImage |= main.ImageParts.Any();
            foundNumbering |= body.Descendants<W.NumberingProperties>().Any();
            foundTable |= body.Elements<W.Table>().Any();
            foundHyperlink |= body.Descendants<W.Hyperlink>().Any();
            Assert.NotNull(main.StyleDefinitionsPart);
        }

        Assert.True(foundImage);
        Assert.True(foundNumbering);
        Assert.True(foundTable);
        Assert.True(foundHyperlink);
    }

    private async Task<CreateJobResponse> CreateJobAsync()
    {
        var response = await _client.PostAsync("/api/v1/jobs", null, CancellationToken.None);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<CreateJobResponse>(Json, CancellationToken.None) ??
               throw new InvalidDataException("Create job response was empty.");
    }

    private async Task<HttpResponseMessage> SendWithAccessAsync(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Job-Token", token);
        return await _client.SendAsync(request, CancellationToken.None);
    }

    private async Task ProcessNextAsync()
    {
        var message = await _factory.Broker.DequeueAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(message);
        var processor = _factory.Services.GetRequiredService<IJobOperationProcessor>();
        await processor.ProcessAsync(message, CancellationToken.None);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
