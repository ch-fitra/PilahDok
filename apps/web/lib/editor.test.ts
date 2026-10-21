import { describe, expect, it } from "vitest";
import { initialMethod, manualSelection, outputSummaries, previewBlocks } from "./editor";
import { buildSegments } from "./segments";
import type { AnalysisResult, SplitCandidate } from "./types";

const candidates: SplitCandidate[] = [
  {
    id: "heading1-2",
    structureIndex: 2,
    sourceParagraphIndex: 2,
    type: "heading1",
    headingLevel: 1,
    title: "Bab pertama",
    confidence: 1,
    selected: true,
    suggestedOutputFileName: "bab.docx",
    warnings: [],
  },
  {
    id: "manual-2",
    structureIndex: 2,
    sourceParagraphIndex: 2,
    type: "manual",
    headingLevel: null,
    title: "Bab pertama",
    confidence: 1,
    selected: true,
    suggestedOutputFileName: "bab.docx",
    warnings: [],
  },
];
const analysis = {
  paragraphs: Array.from({ length: 5 }, (_, index) => ({
    id: `p${index}`,
    paragraphIndex: index,
    structureIndex: index,
    text: `Isi ${index + 1}`,
    previewText: `Isi lengkap ${index + 1}`,
    headingLevel: null,
    isInsideTable: false,
  })),
  splitCandidates: candidates,
  previewImages: [{ structureIndex: 2, dataUrl: "data:image/png;base64,test" }],
} as AnalysisResult;

describe("editor preview mapping", () => {
  it("falls back to manual for a document with no usable chapter boundaries", () => {
    expect(initialMethod([])).toBe("manual");
    expect(initialMethod(candidates.filter((item) => item.type === "manual"))).toBe("manual");
    expect(initialMethod([{ ...candidates[0], structureIndex: 0 }])).toBe("manual");
    expect(initialMethod(candidates)).toBe("heading1");
  });
  it("keeps the same cut positions when editing automatic suggestions by hand", () => {
    const ids = manualSelection(candidates, new Set(["heading1-2"]));
    expect([...ids]).toEqual(["manual-2"]);
    expect(
      buildSegments(
        candidates.filter((item) => item.type === "manual"),
        ids,
      ).map((item) => item.startCandidateId),
    ).toEqual([null, "manual-2"]);
  });
  it("shows source ranges that cover every paragraph once", () => {
    const summaries = outputSummaries(analysis, [
      { startCandidateId: null, outputFileName: "start.docx" },
      { startCandidateId: "manual-2", outputFileName: "rest.docx" },
    ]);
    expect(summaries.map((item) => item.range)).toEqual(["Paragraf 1–2", "Paragraf 3–5"]);
    expect(summaries.map((item) => item.title)).toEqual(["Isi 1", "Isi 3"]);
  });
  it("attaches embedded images and controls to the correct source block", () => {
    const blocks = previewBlocks(analysis);
    expect(blocks[2].images).toEqual(["data:image/png;base64,test"]);
    expect(blocks[2].boundary?.id).toBe("manual-2");
    expect(blocks[0].images).toEqual([]);
  });
});
