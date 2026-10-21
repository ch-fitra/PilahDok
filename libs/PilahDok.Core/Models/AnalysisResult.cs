namespace PilahDok.Core.Models;

public sealed record DocumentMetadata(
    int ParagraphCount,
    int TopLevelParagraphCount,
    int TableCount,
    int ImageCount,
    int SectionCount,
    int ExplicitPageBreakCount);

public sealed record ParagraphInfo(
    string Id,
    int ParagraphIndex,
    int StructureIndex,
    string Text,
    string? StyleId,
    string? StyleName,
    int? OutlineLevel,
    int? HeadingLevel,
    bool HasSectionBoundary,
    bool HasExplicitPageBreak,
    bool IsInsideTable,
    bool CanSplitBefore,
    string? PreviewText = null);

public sealed record DocumentPreviewImage(int StructureIndex, string DataUrl);

public sealed record SplitCandidate(
    string Id,
    int SourceParagraphIndex,
    int StructureIndex,
    SplitCandidateType Type,
    int? HeadingLevel,
    string Title,
    decimal Confidence,
    bool Selected,
    string SuggestedOutputFileName,
    IReadOnlyList<string> Warnings);

public sealed record AnalysisResult(
    string JobId,
    DocumentMetadata Metadata,
    IReadOnlyList<ParagraphInfo> Paragraphs,
    IReadOnlyList<SplitCandidate> SplitCandidates,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, bool> DetectedCapabilities,
    DateTimeOffset AnalyzedAt,
    IReadOnlyList<DocumentPreviewImage>? PreviewImages = null);

public sealed record SplitSegment(string? StartCandidateId, string OutputFileName);

public sealed record SplitRequest(SplitMethod Method, IReadOnlyList<SplitSegment> Segments);

public sealed record OutputArtifact(
    string OutputId,
    string FileName,
    string ContentType,
    long Size,
    string Sha256,
    int Order,
    IReadOnlyList<string> Warnings);

public sealed record ResultManifest(
    string JobId,
    SplitMethod Method,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<OutputArtifact> Files,
    IReadOnlyList<string> Warnings);
