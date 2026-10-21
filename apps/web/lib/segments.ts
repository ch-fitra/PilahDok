import type { SplitCandidate, SplitMethod, SplitSegment } from "./types";

export function candidatesForMethod(
  candidates: SplitCandidate[],
  method: SplitMethod,
): SplitCandidate[] {
  return candidates
    .filter((candidate) => candidate.type === method)
    .sort((left, right) => left.structureIndex - right.structureIndex);
}

export function buildSegments(
  candidates: SplitCandidate[],
  selectedIds: ReadonlySet<string>,
): SplitSegment[] {
  const selected = candidates
    .filter((candidate) => selectedIds.has(candidate.id))
    .sort((left, right) => left.structureIndex - right.structureIndex);
  const result: SplitSegment[] = [];
  const firstAtStart = selected.find((candidate) => candidate.structureIndex === 0);
  if (firstAtStart) {
    result.push({
      startCandidateId: firstAtStart.id,
      outputFileName: numberedName(1, firstAtStart),
    });
  } else {
    result.push({ startCandidateId: null, outputFileName: "01-Bagian-Awal.docx" });
  }

  for (const candidate of selected.filter((item) => item.structureIndex > 0)) {
    result.push({
      startCandidateId: candidate.id,
      outputFileName: numberedName(result.length + 1, candidate),
    });
  }
  return result;
}

function numberedName(order: number, candidate: SplitCandidate): string {
  const prefix = order.toString().padStart(2, "0");
  return `${prefix}-${candidate.suggestedOutputFileName.replace(/^\d{2}[-_]/, "")}`;
}
