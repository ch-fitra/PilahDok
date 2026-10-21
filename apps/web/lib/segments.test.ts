import { describe, expect, it } from "vitest";
import { buildSegments } from "./segments";
import type { SplitCandidate } from "./types";

const candidate = (id: string, structureIndex: number): SplitCandidate => ({
  id,
  sourceParagraphIndex: structureIndex,
  structureIndex,
  type: "heading1",
  headingLevel: 1,
  title: id,
  confidence: 1,
  selected: true,
  suggestedOutputFileName: `${id}.docx`,
  warnings: [],
});

describe("buildSegments", () => {
  it("preserves preface content when the first boundary is after the start", () => {
    const segments = buildSegments(
      [candidate("bab-i", 3), candidate("bab-ii", 8)],
      new Set(["bab-i", "bab-ii"]),
    );
    expect(segments.map((segment) => segment.startCandidateId)).toEqual([null, "bab-i", "bab-ii"]);
  });

  it("uses a candidate at structure index zero as the first segment", () => {
    const segments = buildSegments(
      [candidate("bab-i", 0), candidate("bab-ii", 8)],
      new Set(["bab-i", "bab-ii"]),
    );
    expect(segments.map((segment) => segment.startCandidateId)).toEqual(["bab-i", "bab-ii"]);
  });
});
