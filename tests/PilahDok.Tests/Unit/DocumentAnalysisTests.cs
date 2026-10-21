using Microsoft.Extensions.Logging.Abstractions;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Extensions.Options;
using PilahDok.Core.Errors;
using PilahDok.Infrastructure.Configuration;
using W = DocumentFormat.OpenXml.Wordprocessing;
using PilahDok.Core.Models;
using PilahDok.Infrastructure.DocumentProcessing;
using PilahDok.Testing;

namespace PilahDok.Tests.Unit;

public sealed class DocumentAnalysisTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pilahdok-analysis", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DetectsHeadingChapterSectionAndExplicitBreak()
    {
        var path = Path.Combine(_directory, "fixture.docx");
        FixtureDocumentBuilder.Create(path);
        var engine = new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance);

        var analysis = await engine.AnalyzeAsync(new string('a', 32), path, CancellationToken.None);

        Assert.Equal(3, analysis.SplitCandidates.Count(candidate => candidate.Type == SplitCandidateType.Heading1));
        Assert.Contains(analysis.SplitCandidates, candidate => candidate.Type == SplitCandidateType.Heading2);
        Assert.Equal(3, analysis.SplitCandidates.Count(candidate => candidate.Type == SplitCandidateType.Chapter));
        Assert.Contains(analysis.SplitCandidates, candidate => candidate.Type == SplitCandidateType.Section);
        Assert.Contains(analysis.SplitCandidates, candidate => candidate.Type == SplitCandidateType.PageBreak);
        Assert.True(analysis.Metadata.TableCount >= 1);
        Assert.True(analysis.Metadata.ImageCount >= 1);
        Assert.True(analysis.Metadata.SectionCount >= 2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task PageBreakBeforeHonorsBooleanValueAndDiagnosticCount(bool enabled, int expectedBreakCount)
    {
        var path = Path.Combine(_directory, "disabled-break.docx");
        FixtureDocumentBuilder.Create(path);
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var paragraph = document.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().ElementAt(2);
            paragraph.ParagraphProperties ??= new W.ParagraphProperties();
            paragraph.ParagraphProperties!.PageBreakBefore = new W.PageBreakBefore { Val = enabled };
            document.MainDocumentPart.Document.Save();
        }
        var engine = new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance);
        var analysis = await engine.AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        Assert.Equal(enabled, analysis.Paragraphs[2].HasExplicitPageBreak);
        Assert.Equal(expectedBreakCount, analysis.Metadata.ExplicitPageBreakCount);
        Assert.Equal(enabled, analysis.SplitCandidates.Any(candidate => candidate.Type == SplitCandidateType.PageBreak && candidate.StructureIndex == 2));
    }

    [Fact]
    public async Task SplitMaterializesHeadersInheritedFromAnEarlierSection()
    {
        var path = Path.Combine(_directory, "inherited-header.docx");
        FixtureDocumentBuilder.Create(path);
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var section = document.MainDocumentPart!.Document!.Body!.Elements<W.SectionProperties>().Single();
            section.RemoveAllChildren<W.HeaderReference>();
            section.RemoveAllChildren<W.FooterReference>();
            document.MainDocumentPart.Document.Save();
        }
        var engine = new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance);
        var analysis = await engine.AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        var candidate = analysis.SplitCandidates.Last(item => item.Type == SplitCandidateType.Heading1);
        var outputs = await engine.SplitAsync(path, Path.Combine(_directory, "outputs"), analysis,
            new SplitRequest(SplitMethod.Heading1, [new SplitSegment(null, "start.docx"), new SplitSegment(candidate.Id, "last.docx")]), CancellationToken.None);
        using var result = WordprocessingDocument.Open(Path.Combine(_directory, "outputs", outputs[1].FileName), false);
        var lastSection = result.MainDocumentPart!.Document!.Body!.Elements<W.SectionProperties>().Single();
        Assert.Single(lastSection.Elements<W.HeaderReference>());
        Assert.Single(lastSection.Elements<W.FooterReference>());
    }

    [Fact]
    public async Task SplitRejectsDiskAmplificationBeforeWritingOutputs()
    {
        var path = Path.Combine(_directory, "budget.docx");
        FixtureDocumentBuilder.Create(path);
        var engine = new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance,
            Options.Create(new PilahDokOptions { MaxOutputBytes = 1_024 }));
        var analysis = await engine.AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        var output = Path.Combine(_directory, "over-budget");
        var error = await Assert.ThrowsAsync<PilahDokException>(() => engine.SplitAsync(path, output, analysis,
            new SplitRequest(SplitMethod.Heading1, [new SplitSegment(null, "file.docx")]), CancellationToken.None));
        Assert.Equal("output_too_large", error.PublicCode);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ContentPreviewIncludesLongTextImagesAndSafeWholeTableBoundary()
    {
        var path = Path.Combine(_directory, "preview.docx");
        FixtureDocumentBuilder.Create(path);
        var longText = new string('a', 250) + " akhir paragraf yang masih dapat dibaca";
        using (var document = WordprocessingDocument.Open(path, true))
        {
            document.MainDocumentPart!.Document!.Body!.Elements<W.Paragraph>().First().Append(new W.Run(new W.Text(longText)));
            document.MainDocumentPart.Document.Save();
        }
        var engine = new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance);
        var analysis = await engine.AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        Assert.Contains("akhir paragraf", analysis.Paragraphs[0].PreviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("akhir paragraf", analysis.Paragraphs[0].Text, StringComparison.Ordinal);
        Assert.NotNull(analysis.PreviewImages);
        Assert.Single(analysis.PreviewImages);
        Assert.StartsWith("data:image/png;base64,", analysis.PreviewImages[0].DataUrl, StringComparison.Ordinal);
        var imageBoundary = Assert.Single(analysis.SplitCandidates, item => item.Type == SplitCandidateType.Manual && item.StructureIndex == analysis.PreviewImages[0].StructureIndex);
        Assert.Equal("Gambar dalam dokumen", imageBoundary.Title);
        var tableParagraph = analysis.Paragraphs.First(item => item.IsInsideTable);
        var tableBoundary = Assert.Single(analysis.SplitCandidates, item => item.Type == SplitCandidateType.Manual && item.StructureIndex == tableParagraph.StructureIndex);
        var outputs = await engine.SplitAsync(path, Path.Combine(_directory, "table-output"), analysis,
            new SplitRequest(SplitMethod.Manual, [new SplitSegment(null, "before.docx"), new SplitSegment(tableBoundary.Id, "table.docx")]), CancellationToken.None);
        using var result = WordprocessingDocument.Open(Path.Combine(_directory, "table-output", outputs[1].FileName), false);
        Assert.IsType<W.Table>(result.MainDocumentPart!.Document!.Body!.FirstChild);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewSkipsMislabelledAndOversizedImages(bool oversized)
    {
        var path = Path.Combine(_directory, "unsafe-preview.docx");
        FixtureDocumentBuilder.Create(path);
        using (var document = WordprocessingDocument.Open(path, true))
        {
            var bytes = oversized ? new byte[1024 * 1024 + 1] : "<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8.ToArray();
            if (oversized) new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
            using var source = new MemoryStream(bytes);
            document.MainDocumentPart!.ImageParts.Single().FeedData(source);
        }
        var analysis = await new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance).AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        Assert.NotNull(analysis.PreviewImages);
        Assert.Empty(analysis.PreviewImages);
    }

    [Fact]
    public async Task DocumentWithoutHeadingsStillExposesManualBoundaries()
    {
        var path = Path.Combine(_directory, "no-headings.docx");
        FixtureDocumentBuilder.CreateWithoutHeadings(path);
        var analysis = await new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance).AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        Assert.DoesNotContain(analysis.SplitCandidates, item => item.Type is SplitCandidateType.Heading1 or SplitCandidateType.Heading2 or SplitCandidateType.Chapter);
        Assert.Contains(analysis.SplitCandidates, item => item.Type == SplitCandidateType.Manual && item.StructureIndex > 0);
    }

    [Fact]
    public async Task ContentControlIsNotMislabelledAsTableAndHasOneSafeBoundary()
    {
        var path = Path.Combine(_directory, "content-control.docx");
        FixtureDocumentBuilder.Create(path);
        using (var document = WordprocessingDocument.Open(path, true))
        {
            document.MainDocumentPart!.Document!.Body!.InsertAt(new W.SdtBlock(
                new W.SdtContentBlock(new W.Paragraph(new W.Run(new W.Text("Isi formulir"))))), 1);
            document.MainDocumentPart.Document.Save();
        }
        var analysis = await new OpenXmlDocumentEngine(NullLogger<OpenXmlDocumentEngine>.Instance).AnalyzeAsync(new string('a', 32), path, CancellationToken.None);
        var paragraph = Assert.Single(analysis.Paragraphs, item => item.Text == "Isi formulir");
        Assert.False(paragraph.IsInsideTable);
        Assert.False(paragraph.CanSplitBefore);
        Assert.Single(analysis.SplitCandidates, item => item.Type == SplitCandidateType.Manual && item.StructureIndex == 1);
    }
}
