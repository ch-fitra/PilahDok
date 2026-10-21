using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Errors;
using PilahDok.Infrastructure.Configuration;

namespace PilahDok.Infrastructure.Security;

public sealed class DocxPackageValidator(IOptions<PilahDokOptions> options) : IDocxPackageValidator
{
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private readonly PilahDokOptions _options = options.Value;

    public async Task ValidateAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
        {
            throw Invalid("invalid_docx", "File kosong atau tidak dapat dibaca.");
        }

        if (info.Length > _options.MaxUploadBytes)
        {
            throw Invalid("file_too_large", "Ukuran file melebihi batas yang diizinkan.", 413);
        }

        var signature = new byte[ZipSignature.Length];
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4_096, true))
        {
            if (await stream.ReadAsync(signature, cancellationToken) != signature.Length || !signature.SequenceEqual(ZipSignature))
            {
                throw Invalid("invalid_docx", "File bukan paket DOCX yang valid.");
            }
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            ValidateArchive(archive, cancellationToken);
        }
        catch (PilahDokException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw Invalid("invalid_docx", "Paket DOCX rusak atau tidak didukung.", innerException: exception);
        }
    }

    private void ValidateArchive(ZipArchive archive, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > _options.MaxZipEntries)
        {
            throw Invalid("too_many_archive_entries", "Dokumen memiliki terlalu banyak bagian internal.");
        }

        long totalUncompressed = 0;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateEntryPath(entry.FullName);
            if (!names.Add(entry.FullName))
            {
                throw Invalid("duplicate_archive_entry", "Dokumen memiliki bagian internal duplikat.");
            }

            checked
            {
                totalUncompressed += entry.Length;
            }

            if (totalUncompressed > _options.MaxUncompressedBytes)
            {
                throw Invalid("archive_too_large", "Ukuran dokumen setelah dekompresi melebihi batas aman.");
            }

            if (entry.Length > 0 && entry.CompressedLength == 0)
            {
                throw Invalid("suspicious_compression", "Rasio kompresi dokumen tidak aman.");
            }

            if (entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > _options.MaxCompressionRatio)
            {
                throw Invalid("suspicious_compression", "Rasio kompresi dokumen tidak aman.");
            }

            if (IsNestedArchive(entry.FullName) || entry.FullName.StartsWith("word/embeddings/", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("embedded_content_not_supported", "Dokumen dengan arsip atau objek tertanam belum didukung.");
            }

            if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid("macro_not_supported", "Dokumen yang mengandung macro tidak didukung.");
            }

            if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                ValidateXml(entry);
            }
        }

        var required = new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml" };
        if (required.Any(name => !names.Contains(name)))
        {
            throw Invalid("invalid_docx_structure", "Struktur minimum Office Open XML tidak ditemukan.");
        }

        ValidateContentTypes(archive.GetEntry("[Content_Types].xml")!);
    }

    public static void ValidateEntryPath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) || entryName.Contains('\0') || entryName.Contains('\\'))
        {
            throw Invalid("unsafe_archive_path", "Dokumen memiliki path internal yang tidak aman.");
        }

        if (entryName.StartsWith('/') || entryName.Split('/').Any(part => part is ".." or "."))
        {
            throw Invalid("unsafe_archive_path", "Dokumen memiliki path internal yang tidak aman.");
        }

        if (Uri.TryCreate(entryName, UriKind.Absolute, out _))
        {
            throw Invalid("unsafe_archive_path", "Dokumen memiliki path internal yang tidak aman.");
        }
    }

    private static void ValidateXml(ZipArchiveEntry entry)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = 50_000_000,
            IgnoreComments = true
        };

        try
        {
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
            }
        }
        catch (XmlException exception)
        {
            throw Invalid("unsafe_or_malformed_xml", "Dokumen memuat XML rusak atau tidak aman.", innerException: exception);
        }
    }

    private static void ValidateContentTypes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        var contentTypes = document.Descendants().Attributes("ContentType").Select(attribute => attribute.Value).ToArray();
        if (!contentTypes.Contains(
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
                StringComparer.Ordinal))
        {
            throw Invalid("unsupported_office_format", "Paket Office ini bukan dokumen DOCX tanpa macro.");
        }

        if (contentTypes.Any(type => type.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid("macro_not_supported", "Dokumen yang mengandung macro tidak didukung.");
        }
    }

    private static bool IsNestedArchive(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".docm", StringComparison.OrdinalIgnoreCase);
    }

    private static PilahDokException Invalid(
        string code,
        string message,
        int statusCode = 422,
        Exception? innerException = null) => new(code, message, statusCode, innerException);
}
