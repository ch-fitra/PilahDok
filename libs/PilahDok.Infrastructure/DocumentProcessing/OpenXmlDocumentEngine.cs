using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PilahDok.Core.Abstractions;
using PilahDok.Core.Errors;
using PilahDok.Core.Models;
using PilahDok.Core.Services;
using PilahDok.Infrastructure.Configuration;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PilahDok.Infrastructure.DocumentProcessing;

public sealed partial class OpenXmlDocumentEngine(
    ILogger<OpenXmlDocumentEngine> logger,
    IOptions<PilahDokOptions>? options = null) : IDocumentEngine
{
    public async Task<AnalysisResult> AnalyzeAsync(string jobId, string inputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = WordprocessingDocument.Open(inputPath, false);
        var mainPart = document.MainDocumentPart ?? throw InvalidDocument();
        var mainDocument = mainPart.Document ?? throw InvalidDocument();
        var body = mainDocument.Body ?? throw InvalidDocument();
        var styleMap = BuildStyleMap(mainPart);
        var structure = body.Elements().ToList();
        var allParagraphs = body.Descendants<W.Paragraph>().ToList();
        var paragraphIndexes = new Dictionary<W.Paragraph, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < allParagraphs.Count; index++)
        {
            paragraphIndexes.Add(allParagraphs[index], index);
        }

        var paragraphs = new List<ParagraphInfo>(allParagraphs.Count);
        var candidates = new List<SplitCandidate>();
        var warnings = new List<string>();
        var sectionBoundaryCount = 0;
        var explicitBreakCount = 0;
        var topLevelParagraphCount = 0;
        var previewImages = new List<DocumentPreviewImage>();
        var previewTextRemaining = 500_000;
        var previewBytesRemaining = 4 * 1024 * 1024;

        for (var structureIndex = 0; structureIndex < structure.Count; structureIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = structure[structureIndex];
            foreach (var blip in block.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().Take(32))
            {
                if (previewImages.Count >= 16 || previewBytesRemaining <= 0) break;
                if (blip.Embed?.Value is not { } relationshipId ||
                    !mainPart.TryGetPartById(relationshipId, out var part) || part is not ImagePart image) continue;
                var data = await ReadPreviewImageAsync(image, Math.Min(1024 * 1024, previewBytesRemaining), cancellationToken);
                if (data is null) continue;
                previewBytesRemaining -= data.Length;
                previewImages.Add(new DocumentPreviewImage(structureIndex, $"data:{image.ContentType};base64,{Convert.ToBase64String(data)}"));
            }
            var blockParagraphs = block is W.Paragraph paragraph ? [paragraph] : block.Descendants<W.Paragraph>().ToArray();
            foreach (var current in blockParagraphs)
            {
                var isTopLevel = ReferenceEquals(current, block);
                if (isTopLevel)
                {
                    topLevelParagraphCount++;
                }

                var paragraphIndex = paragraphIndexes[current];
                var text = SafeFileName.NormalizeTitle(current.InnerText);
                var previewText = current.InnerText;
                var previewLimit = Math.Min(4_000, previewTextRemaining);
                if (previewText.Length > previewLimit) previewText = previewText[..previewLimit] + "…";
                previewTextRemaining -= Math.Min(current.InnerText.Length, previewLimit);
                var styleId = current.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
                styleMap.TryGetValue(styleId ?? string.Empty, out var style);
                var outlineLevel = GetOutlineLevel(current, style);
                var headingLevel = GetHeadingLevel(styleId, style?.Name, outlineLevel);
                var hasSectionBoundary = current.ParagraphProperties?.GetFirstChild<W.SectionProperties>() is not null;
                var hasPageBreak = HasExplicitPageBreak(current);

                if (hasSectionBoundary)
                {
                    sectionBoundaryCount++;
                }

                if (hasPageBreak)
                {
                    explicitBreakCount += current.Descendants<W.Break>()
                        .Count(element => element.Type?.Value == W.BreakValues.Page);
                    if (IsPageBreakBefore(current)) explicitBreakCount++;
                }

                paragraphs.Add(new ParagraphInfo(
                    $"paragraph-{paragraphIndex.ToString(CultureInfo.InvariantCulture)}",
                    paragraphIndex,
                    structureIndex,
                    text,
                    styleId,
                    style?.Name,
                    outlineLevel,
                    headingLevel,
                    hasSectionBoundary,
                    hasPageBreak,
                    current.Ancestors<W.Table>().Any(),
                    isTopLevel && structureIndex > 0,
                    previewText));

                if (!isTopLevel)
                {
                    continue;
                }

                AddManualCandidate(candidates, paragraphIndex, structureIndex,
                    !string.IsNullOrWhiteSpace(text) ? text : current.Descendants<W.Drawing>().Any() ? "Gambar dalam dokumen" : "Baris kosong");

                if (headingLevel is 1 or 2)
                {
                    AddCandidate(
                        candidates,
                        headingLevel == 1 ? SplitCandidateType.Heading1 : SplitCandidateType.Heading2,
                        paragraphIndex,
                        structureIndex,
                        headingLevel,
                        text,
                        1.0m,
                        []);
                }

                if (IsChapterTitle(text))
                {
                    AddCandidate(
                        candidates,
                        SplitCandidateType.Chapter,
                        paragraphIndex,
                        structureIndex,
                        headingLevel,
                        text,
                        headingLevel is not null ? 0.95m : 0.75m,
                        headingLevel is null ? ["Kandidat bab dideteksi dari pola teks, bukan style Heading."] : []);
                }

                if (IsPageBreakBefore(current))
                {
                    AddCandidate(
                        candidates,
                        SplitCandidateType.PageBreak,
                        paragraphIndex,
                        structureIndex,
                        headingLevel,
                        string.IsNullOrWhiteSpace(text) ? "Setelah page break" : text,
                        0.9m,
                        []);
                }

                var inlineBreaks = current.Descendants<W.Break>()
                    .Count(element => element.Type?.Value == W.BreakValues.Page);
                if (inlineBreaks > 0 && structureIndex + 1 < structure.Count)
                {
                    var next = FindFirstParagraph(structure, structureIndex + 1);
                    AddCandidate(
                        candidates,
                        SplitCandidateType.PageBreak,
                        next is null ? paragraphIndex : paragraphIndexes[next],
                        structureIndex + 1,
                        null,
                        next is null ? "Setelah page break" : SafeFileName.NormalizeTitle(next.InnerText),
                        0.85m,
                        ["Page break berada di dalam paragraf; batas aman ditempatkan sesudah paragraf tersebut."]);
                }

                if (hasSectionBoundary && structureIndex + 1 < structure.Count)
                {
                    var next = FindFirstParagraph(structure, structureIndex + 1);
                    AddCandidate(
                        candidates,
                        SplitCandidateType.Section,
                        next is null ? paragraphIndex : paragraphIndexes[next],
                        structureIndex + 1,
                        null,
                        next is null ? $"Bagian {sectionBoundaryCount + 1}" : SafeFileName.NormalizeTitle(next.InnerText),
                        1.0m,
                        []);
                }
            }
            if (block is not W.Paragraph && blockParagraphs.FirstOrDefault() is { } first)
            {
                AddManualCandidate(candidates, paragraphIndexes[first], structureIndex,
                    $"{(block is W.Table ? "Tabel" : "Bagian")} — {SafeFileName.NormalizeTitle(first.InnerText)}");
            }
        }

        if (structure.OfType<W.Table>().Any(table => table.Descendants<W.TableRow>().Count() > 20))
        {
            warnings.Add("Dokumen memiliki tabel panjang yang dapat mengalami reflow setelah dipisahkan.");
        }

        if (mainPart.ExternalRelationships.Any() || mainPart.HyperlinkRelationships.Any(relationship => relationship.IsExternal))
        {
            warnings.Add("Dokumen memiliki relationship eksternal. PilahDok tidak mengambil konten dari alamat eksternal.");
        }

        warnings.Add("Pemisahan mempertahankan package asli: media, komentar, dan metadata dari bagian lain dapat tetap tersimpan. Periksa hasil sebelum membagikan dokumen sensitif.");

        if (!candidates.Any(candidate => candidate.Type is not SplitCandidateType.Manual))
        {
            warnings.Add("Tidak ada batas struktural otomatis yang ditemukan. Gunakan titik pemisahan manual.");
        }

        var imageCount = CountImages(mainPart);
        var metadata = new DocumentMetadata(
            allParagraphs.Count,
            topLevelParagraphCount,
            structure.OfType<W.Table>().Count(),
            imageCount,
            sectionBoundaryCount + 1,
            explicitBreakCount);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["structuralSplitting"] = true,
            ["visualPagination"] = false,
            ["pdfPreview"] = false
        };

        return new AnalysisResult(
            jobId,
            metadata,
            paragraphs,
            candidates
                .OrderBy(candidate => candidate.StructureIndex)
                .ThenBy(candidate => candidate.Type)
                .ToArray(),
            warnings,
            capabilities,
            DateTimeOffset.UtcNow,
            previewImages);
    }

    private static async Task<byte[]?> ReadPreviewImageAsync(ImagePart image, int maximumBytes, CancellationToken cancellationToken)
    {
        if (image.ContentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) return null;
        await using var stream = image.GetStream(FileMode.Open, FileAccess.Read);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximumBytes) return null;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        var valid = image.ContentType switch
        {
            "image/png" => bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => bytes.AsSpan().StartsWith(new byte[] { 255, 216, 255 }),
            "image/gif" => bytes.AsSpan().StartsWith("GIF8"u8),
            "image/webp" => bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        return valid ? bytes : null;
    }

    public async Task<IReadOnlyList<OutputArtifact>> SplitAsync(
        string inputPath,
        string outputDirectory,
        AnalysisResult analysis,
        SplitRequest request,
        CancellationToken cancellationToken)
    {
        var resolvedSegments = ResolveSegments(analysis, request);
        var outputBudget = options?.Value.MaxOutputBytes ?? new PilahDokOptions().MaxOutputBytes;
        // Each output starts as a full package clone. Bound disk amplification before writing.
        if (new FileInfo(inputPath).Length * resolvedSegments.Count > outputBudget)
        {
            throw new PilahDokException("output_too_large", "Total hasil melebihi batas aman. Kurangi jumlah bagian atau ukuran dokumen.", 422);
        }
        Directory.CreateDirectory(outputDirectory);
        var safeNames = SafeFileName.MakeUnique(resolvedSegments.Select(segment => segment.OutputFileName));
        var artifacts = new List<OutputArtifact>(resolvedSegments.Count);

        using var source = WordprocessingDocument.Open(inputPath, false);
        var sourceMainPart = source.MainDocumentPart ?? throw InvalidDocument();
        var sourceDocument = sourceMainPart.Document ?? throw InvalidDocument();
        var sourceBody = sourceDocument.Body ?? throw InvalidDocument();
        var sourceStructure = sourceBody.Elements().Where(element => element is not W.SectionProperties).ToList();
        var terminalSection = sourceBody.Elements<W.SectionProperties>().LastOrDefault();

        for (var index = 0; index < resolvedSegments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = resolvedSegments[index];
            var start = segment.StartIndex;
            var end = index + 1 < resolvedSegments.Count ? resolvedSegments[index + 1].StartIndex : sourceStructure.Count;
            if (start < 0 || start >= sourceStructure.Count || end <= start)
            {
                throw new PilahDokException("invalid_split_range", "Titik pemisahan menghasilkan rentang kosong atau tidak valid.", 422);
            }

            var outputPath = Path.Combine(outputDirectory, safeNames[index]);
            await CopyFileAsync(inputPath, outputPath, cancellationToken);
            TrimDocument(outputPath, start, end, sourceStructure, terminalSection);
            await ValidateOutputAsync(outputPath, cancellationToken);

            var info = new FileInfo(outputPath);
            if (info.Length + artifacts.Sum(artifact => artifact.Size) > outputBudget)
            {
                throw new PilahDokException("output_too_large", "Total hasil melebihi batas aman. Kurangi jumlah bagian atau ukuran dokumen.", 422);
            }
            await using var checksumStream = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var checksum = Convert.ToHexStringLower(await SHA256.HashDataAsync(checksumStream, cancellationToken));
            artifacts.Add(new OutputArtifact(
                Guid.NewGuid().ToString("N"),
                safeNames[index],
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                info.Length,
                checksum,
                index + 1,
                segment.Warnings));
        }

        return artifacts;
    }

    public Task ValidateOutputAsync(string outputPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = WordprocessingDocument.Open(outputPath, false);
        if (document.MainDocumentPart?.Document?.Body is null)
        {
            throw new PilahDokException("invalid_output", "Salah satu hasil DOCX tidak memiliki struktur dokumen utama.", 500);
        }

        var validator = new OpenXmlValidator(FileFormatVersions.Office2013);
        var errors = validator.Validate(document, cancellationToken).Take(10).ToArray();
        if (errors.Length > 0)
        {
            LogOutputValidationFailure(logger, errors.Length);
            throw new PilahDokException("invalid_output", "Salah satu hasil DOCX gagal divalidasi.", 500);
        }

        return Task.CompletedTask;
    }

    private static List<ResolvedSegment> ResolveSegments(AnalysisResult analysis, SplitRequest request)
    {
        if (request.Segments.Count is < 1 or > 200)
        {
            throw new PilahDokException("invalid_segments", "Jumlah bagian harus antara 1 dan 200.", 422);
        }

        var candidates = analysis.SplitCandidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        var resolved = new List<ResolvedSegment>(request.Segments.Count);
        foreach (var segment in request.Segments)
        {
            if (segment.StartCandidateId is null)
            {
                resolved.Add(new ResolvedSegment(0, segment.OutputFileName, []));
                continue;
            }

            if (!candidates.TryGetValue(segment.StartCandidateId, out var candidate))
            {
                throw new PilahDokException("unknown_split_point", "Salah satu titik pemisahan tidak ditemukan.", 422);
            }

            if (!CandidateMatchesMethod(candidate.Type, request.Method))
            {
                throw new PilahDokException("split_method_mismatch", "Titik pemisahan tidak cocok dengan metode yang dipilih.", 422);
            }

            resolved.Add(new ResolvedSegment(candidate.StructureIndex, segment.OutputFileName, candidate.Warnings));
        }

        if (resolved[0].StartIndex != 0 || resolved.Select(segment => segment.StartIndex).Distinct().Count() != resolved.Count)
        {
            throw new PilahDokException("invalid_segment_order", "Bagian pertama harus dimulai dari awal dan setiap titik harus unik.", 422);
        }

        if (!resolved.SequenceEqual(resolved.OrderBy(segment => segment.StartIndex)))
        {
            throw new PilahDokException("invalid_segment_order", "Titik pemisahan harus berurutan.", 422);
        }

        if (resolved.Any(segment => string.IsNullOrWhiteSpace(segment.OutputFileName) || segment.OutputFileName.Length > 140))
        {
            throw new PilahDokException("invalid_output_name", "Nama file hasil wajib diisi dan maksimal 140 karakter.", 422);
        }

        return resolved;
    }

    private static bool CandidateMatchesMethod(SplitCandidateType candidate, SplitMethod method) => method switch
    {
        SplitMethod.Heading1 => candidate == SplitCandidateType.Heading1,
        SplitMethod.Heading2 => candidate == SplitCandidateType.Heading2,
        SplitMethod.Chapter => candidate == SplitCandidateType.Chapter,
        SplitMethod.Section => candidate == SplitCandidateType.Section,
        SplitMethod.PageBreak => candidate == SplitCandidateType.PageBreak,
        SplitMethod.Manual => candidate == SplitCandidateType.Manual,
        _ => false
    };

    private static void TrimDocument(
        string outputPath,
        int start,
        int end,
        IReadOnlyList<OpenXmlElement> sourceStructure,
        W.SectionProperties? terminalSection)
    {
        var effectiveSection = FindEffectiveSection(sourceStructure, end - 1, terminalSection);
        using var output = WordprocessingDocument.Open(outputPath, true);
        var main = output.MainDocumentPart ?? throw InvalidDocument();
        var mainDocument = main.Document ?? throw InvalidDocument();
        var body = mainDocument.Body ?? throw InvalidDocument();
        var content = body.Elements().Where(element => element is not W.SectionProperties).ToList();

        for (var index = content.Count - 1; index >= 0; index--)
        {
            if (index < start || index >= end)
            {
                content[index].Remove();
            }
            else if (content[index] is W.Paragraph paragraph &&
                     paragraph.ParagraphProperties?.GetFirstChild<W.SectionProperties>() is { } section)
            {
                var resolved = FindEffectiveSection(sourceStructure, index, terminalSection);
                if (resolved is not null)
                {
                    paragraph.ParagraphProperties!.ReplaceChild(resolved.CloneNode(true), section);
                }
            }
        }

        foreach (var section in body.Elements<W.SectionProperties>().ToArray())
        {
            section.Remove();
        }

        var lastParagraph = body.Elements().LastOrDefault() as W.Paragraph;
        lastParagraph?.ParagraphProperties?.GetFirstChild<W.SectionProperties>()?.Remove();
        body.Append(effectiveSection is null ? new W.SectionProperties() : (W.SectionProperties)effectiveSection.CloneNode(true));
        main.Document.Save();
    }

    private static W.SectionProperties? FindEffectiveSection(
        IReadOnlyList<OpenXmlElement> structure,
        int contentIndex,
        W.SectionProperties? terminal)
    {
        var preceding = new List<W.SectionProperties>();
        for (var index = 0; index < structure.Count; index++)
        {
            if (structure[index] is W.Paragraph paragraph &&
                paragraph.ParagraphProperties?.GetFirstChild<W.SectionProperties>() is { } properties)
            {
                preceding.Add(properties);
                if (index >= contentIndex)
                {
                    return ResolveInheritedReferences(preceding);
                }
            }
        }
        if (terminal is not null) preceding.Add(terminal);
        return preceding.Count == 0 ? null : ResolveInheritedReferences(preceding);
    }

    private static W.SectionProperties ResolveInheritedReferences(IReadOnlyList<W.SectionProperties> sections)
    {
        var resolved = (W.SectionProperties)sections[^1].CloneNode(true);
        var headers = new Dictionary<W.HeaderFooterValues, W.HeaderReference>();
        var footers = new Dictionary<W.HeaderFooterValues, W.FooterReference>();
        foreach (var section in sections.Reverse())
        {
            foreach (var reference in section.Elements<W.HeaderReference>())
            {
                if (reference.Type?.Value is { } type) headers.TryAdd(type, reference);
            }
            foreach (var reference in section.Elements<W.FooterReference>())
            {
                if (reference.Type?.Value is { } type) footers.TryAdd(type, reference);
            }
        }
        resolved.RemoveAllChildren<W.HeaderReference>();
        resolved.RemoveAllChildren<W.FooterReference>();
        foreach (var reference in footers.Values.Reverse()) resolved.PrependChild(reference.CloneNode(true));
        foreach (var reference in headers.Values.Reverse()) resolved.PrependChild(reference.CloneNode(true));
        return resolved;
    }

    private static Dictionary<string, StyleInfo> BuildStyleMap(MainDocumentPart mainPart)
    {
        return mainPart.StyleDefinitionsPart?.Styles?.Elements<W.Style>()
            .Where(style => style.StyleId?.Value is not null)
            .ToDictionary(
                style => style.StyleId!.Value!,
                style => new StyleInfo(
                    style.StyleName?.Val?.Value,
                    style.StyleParagraphProperties?.OutlineLevel?.Val?.Value),
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, StyleInfo>(StringComparer.OrdinalIgnoreCase);
    }

    private static int? GetOutlineLevel(W.Paragraph paragraph, StyleInfo? style)
    {
        return paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value ?? style?.OutlineLevel;
    }

    private static int? GetHeadingLevel(string? styleId, string? styleName, int? outlineLevel)
    {
        if (outlineLevel is >= 0 and <= 8)
        {
            return outlineLevel + 1;
        }

        var match = HeadingStyle().Match($"{styleId} {styleName}");
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var level)
            ? Math.Clamp(level, 1, 9)
            : null;
    }

    private static bool HasExplicitPageBreak(W.Paragraph paragraph)
    {
        return IsPageBreakBefore(paragraph) ||
               paragraph.Descendants<W.Break>().Any(element => element.Type?.Value == W.BreakValues.Page);
    }

    private static bool IsPageBreakBefore(W.Paragraph paragraph) =>
        paragraph.ParagraphProperties?.PageBreakBefore is { } pageBreak && (pageBreak.Val?.Value ?? true);

    private static bool IsChapterTitle(string text)
    {
        return text.Length is > 0 and <= 200 && ChapterTitle().IsMatch(text);
    }

    private static W.Paragraph? FindFirstParagraph(List<OpenXmlElement> structure, int start)
    {
        for (var index = start; index < structure.Count; index++)
        {
            if (structure[index] is W.Paragraph paragraph)
            {
                return paragraph;
            }

            var nested = structure[index].Descendants<W.Paragraph>().FirstOrDefault();
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private static void AddManualCandidate(
        ICollection<SplitCandidate> candidates,
        int paragraphIndex,
        int structureIndex,
        string title)
    {
        AddCandidate(candidates, SplitCandidateType.Manual, paragraphIndex, structureIndex, null, title, 1.0m, []);
    }

    private static void AddCandidate(
        ICollection<SplitCandidate> candidates,
        SplitCandidateType type,
        int paragraphIndex,
        int structureIndex,
        int? headingLevel,
        string title,
        decimal confidence,
        IReadOnlyList<string> warnings)
    {
        var normalizedTitle = string.IsNullOrWhiteSpace(title) ? "Bagian tanpa judul" : SafeFileName.NormalizeTitle(title);
        var id = $"{type.ToString().ToLowerInvariant()}-{structureIndex.ToString(CultureInfo.InvariantCulture)}";
        if (candidates.Any(candidate => candidate.Id.Equals(id, StringComparison.Ordinal)))
        {
            return;
        }

        candidates.Add(new SplitCandidate(
            id,
            paragraphIndex,
            structureIndex,
            type,
            headingLevel,
            normalizedTitle,
            confidence,
            true,
            SafeFileName.Sanitize(normalizedTitle),
            warnings));
    }

    private static int CountImages(MainDocumentPart mainPart)
    {
        return mainPart.ImageParts.Count() +
               mainPart.HeaderParts.Sum(part => part.ImageParts.Count()) +
               mainPart.FooterParts.Sum(part => part.ImageParts.Count());
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static PilahDokException InvalidDocument() =>
        new("invalid_docx_structure", "Dokumen tidak memiliki bagian utama yang dapat diproses.", 422);

    private sealed record StyleInfo(string? Name, int? OutlineLevel);
    private sealed record ResolvedSegment(int StartIndex, string OutputFileName, IReadOnlyList<string> Warnings);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Error, Message = "Generated DOCX failed Open XML validation with {ErrorCount} error(s)")]
    private static partial void LogOutputValidationFailure(ILogger logger, int errorCount);

    [GeneratedRegex("(?:heading|judul)\\s*([1-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingStyle();

    [GeneratedRegex("^(?:BAB|CHAPTER)\\s+(?:[IVXLCDM]+|\\d+)(?:\\s*[:.\\-–—]?\\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterTitle();
}
