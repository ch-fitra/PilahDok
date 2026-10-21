export type JobSecrets = {
  jobId: string;
  accessToken: string;
  downloadToken: string;
  expiresAt: string;
};

export type JobStatus = {
  jobId: string;
  status: string;
  progress: number;
  stage: string;
  createdAt: string;
  updatedAt: string;
  expiresAt: string;
  originalFileName: string | null;
  fileSize: number;
  splitMethod: string | null;
  publicErrorCode: string | null;
  publicErrorMessage: string | null;
  downloadAvailable: boolean;
  resultSize: number | null;
};

export type SplitMethod = "heading1" | "heading2" | "chapter" | "section" | "pageBreak" | "manual";

export type SplitCandidate = {
  id: string;
  sourceParagraphIndex: number;
  structureIndex: number;
  type: SplitMethod;
  headingLevel: number | null;
  title: string;
  confidence: number;
  selected: boolean;
  suggestedOutputFileName: string;
  warnings: string[];
};

export type ParagraphInfo = {
  id: string;
  paragraphIndex: number;
  structureIndex: number;
  text: string;
  styleId: string | null;
  styleName: string | null;
  outlineLevel: number | null;
  headingLevel: number | null;
  hasSectionBoundary: boolean;
  hasExplicitPageBreak: boolean;
  isInsideTable: boolean;
  canSplitBefore: boolean;
  previewText?: string | null;
};

export type AnalysisResult = {
  jobId: string;
  metadata: {
    paragraphCount: number;
    topLevelParagraphCount: number;
    tableCount: number;
    imageCount: number;
    sectionCount: number;
    explicitPageBreakCount: number;
  };
  paragraphs: ParagraphInfo[];
  splitCandidates: SplitCandidate[];
  warnings: string[];
  detectedCapabilities: Record<string, boolean>;
  analyzedAt: string;
  previewImages?: { structureIndex: number; dataUrl: string }[] | null;
};

export type SplitSegment = {
  startCandidateId: string | null;
  outputFileName: string;
};

export type Capabilities = {
  supportedInputFormats: string[];
  structuralSplitMethods: string[];
  maxUploadBytes: number;
  jobTtlMinutes: number;
  visualPagination: { available: boolean; provider: string; explanation: string };
  antivirusEnabled: boolean;
};
