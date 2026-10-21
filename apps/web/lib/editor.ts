import type {
  AnalysisResult,
  ParagraphInfo,
  SplitCandidate,
  SplitMethod,
  SplitSegment,
} from "./types";

export function initialMethod(candidates: SplitCandidate[]): SplitMethod {
  return (
    (["heading1", "chapter", "heading2"] as const).find((method) =>
      candidates.some((candidate) => candidate.type === method && candidate.structureIndex > 0),
    ) ?? "manual"
  );
}

export function manualSelection(
  candidates: SplitCandidate[],
  selectedIds: ReadonlySet<string>,
): Set<string> {
  const indices = new Set(
    candidates.filter((item) => selectedIds.has(item.id)).map((item) => item.structureIndex),
  );
  return new Set(
    candidates
      .filter((item) => item.type === "manual" && indices.has(item.structureIndex))
      .map((item) => item.id),
  );
}

type PreviewBlock = {
  structureIndex: number;
  paragraphs: ParagraphInfo[];
  images: string[];
  boundary?: SplitCandidate;
};

export function previewBlocks(analysis: AnalysisResult): PreviewBlock[] {
  const grouped = new Map<number, PreviewBlock>();
  for (const paragraph of analysis.paragraphs) {
    const block = grouped.get(paragraph.structureIndex) ?? {
      structureIndex: paragraph.structureIndex,
      paragraphs: [],
      images: [],
    };
    block.paragraphs.push(paragraph);
    grouped.set(paragraph.structureIndex, block);
  }
  for (const image of analysis.previewImages ?? []) {
    grouped.get(image.structureIndex)?.images.push(image.dataUrl);
  }
  for (const candidate of analysis.splitCandidates) {
    if (candidate.type === "manual") {
      const block = grouped.get(candidate.structureIndex);
      if (block) block.boundary = candidate;
    }
  }
  return [...grouped.values()].sort((a, b) => a.structureIndex - b.structureIndex);
}

export function outputSummaries(analysis: AnalysisResult, segments: SplitSegment[]) {
  const candidates = new Map(analysis.splitCandidates.map((item) => [item.id, item]));
  const starts = segments.map((segment) =>
    segment.startCandidateId ? (candidates.get(segment.startCandidateId)?.structureIndex ?? 0) : 0,
  );
  return segments.map((segment, index) => {
    const paragraphs = analysis.paragraphs.filter(
      (item) =>
        item.structureIndex >= starts[index] &&
        (index === starts.length - 1 || item.structureIndex < starts[index + 1]),
    );
    return {
      title: paragraphs.find((item) => item.text.trim())?.text ?? "Gambar atau bagian tanpa teks",
      range: paragraphs.length
        ? `Paragraf ${paragraphs[0].paragraphIndex + 1}–${paragraphs.at(-1)!.paragraphIndex + 1}`
        : "Bagian dokumen",
      startIndex: starts[index],
      segment,
    };
  });
}
