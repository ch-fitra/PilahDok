using System.IO.Compression;
using Microsoft.Extensions.Options;
using PilahDok.Core.Errors;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.Security;
using PilahDok.Testing;

namespace PilahDok.Tests.Security;

public sealed class DocxValidatorSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pilahdok-security", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcceptsValidDocxPackage()
    {
        var path = Path.Combine(_directory, "valid.docx");
        FixtureDocumentBuilder.Create(path);
        await CreateValidator().ValidateAsync(path, CancellationToken.None);
    }

    [Fact]
    public async Task RejectsMalformedZip()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "malformed.docx");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 0x01], CancellationToken.None);
        var exception = await Assert.ThrowsAsync<PilahDokException>(() =>
            CreateValidator().ValidateAsync(path, CancellationToken.None));
        Assert.Equal("invalid_docx", exception.PublicCode);
    }

    [Fact]
    public async Task RejectsZipSlipEntry()
    {
        var path = CreateArchive((archive) =>
        {
            AddRequiredEntries(archive);
            archive.CreateEntry("../escape.xml");
        });
        var exception = await Assert.ThrowsAsync<PilahDokException>(() =>
            CreateValidator().ValidateAsync(path, CancellationToken.None));
        Assert.Equal("unsafe_archive_path", exception.PublicCode);
    }

    [Fact]
    public async Task RejectsTooManyEntries()
    {
        var path = CreateArchive((archive) =>
        {
            AddRequiredEntries(archive);
            for (var index = 0; index < 10; index++)
            {
                archive.CreateEntry($"custom/item-{index}.txt");
            }
        });
        var exception = await Assert.ThrowsAsync<PilahDokException>(() =>
            CreateValidator(maxEntries: 5).ValidateAsync(path, CancellationToken.None));
        Assert.Equal("too_many_archive_entries", exception.PublicCode);
    }

    [Fact]
    public async Task RejectsNestedArchive()
    {
        var path = CreateArchive((archive) =>
        {
            AddRequiredEntries(archive);
            archive.CreateEntry("word/embeddings/payload.zip");
        });
        var exception = await Assert.ThrowsAsync<PilahDokException>(() =>
            CreateValidator().ValidateAsync(path, CancellationToken.None));
        Assert.Equal("embedded_content_not_supported", exception.PublicCode);
    }

    [Fact]
    public async Task RejectsXmlWithDoctype()
    {
        var path = CreateArchive((archive) =>
        {
            AddText(archive, "[Content_Types].xml", ContentTypes);
            AddText(archive, "_rels/.rels", Relationships);
            AddText(archive, "word/document.xml", "<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><foo>&xxe;</foo>");
        });
        var exception = await Assert.ThrowsAsync<PilahDokException>(() =>
            CreateValidator().ValidateAsync(path, CancellationToken.None));
        Assert.Equal("unsafe_or_malformed_xml", exception.PublicCode);
    }

    [Theory]
    [InlineData("../evil.xml")]
    [InlineData("/absolute.xml")]
    [InlineData("word\\evil.xml")]
    [InlineData("https://evil.invalid/file.xml")]
    public void PathValidationRejectsTraversalAndAbsolutePaths(string value)
    {
        var exception = Assert.Throws<PilahDokException>(() => DocxPackageValidator.ValidateEntryPath(value));
        Assert.Equal("unsafe_archive_path", exception.PublicCode);
    }

    private DocxPackageValidator CreateValidator(int maxEntries = 2_048)
    {
        return new DocxPackageValidator(Options.Create(new PilahDokOptions
        {
            MaxUploadBytes = 52_428_800,
            MaxZipEntries = maxEntries,
            MaxUncompressedBytes = 209_715_200,
            MaxCompressionRatio = 100,
            StorageRoot = _directory,
            RedisConnection = "localhost"
        }));
    }

    private string CreateArchive(Action<ZipArchive> populate)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.docx");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        populate(archive);
        return path;
    }

    private static void AddRequiredEntries(ZipArchive archive)
    {
        AddText(archive, "[Content_Types].xml", ContentTypes);
        AddText(archive, "_rels/.rels", Relationships);
        AddText(archive, "word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body/></w:document>");
    }

    private static void AddText(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private const string ContentTypes = "<Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Override PartName='/word/document.xml' ContentType='application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml'/></Types>";
    private const string Relationships = "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='word/document.xml'/></Relationships>";

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
