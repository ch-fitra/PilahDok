namespace PilahDok.Core.Models;

public enum JobStatus
{
    Created,
    Uploading,
    Validating,
    Uploaded,
    Queued,
    Analyzing,
    Ready,
    Processing,
    Completed,
    Failed,
    Expired,
    Deleted
}

public enum JobOperation
{
    Analyze,
    Split
}

public enum SplitMethod
{
    Heading1,
    Heading2,
    Chapter,
    Section,
    PageBreak,
    Manual
}

public enum SplitCandidateType
{
    Heading1,
    Heading2,
    Chapter,
    Section,
    PageBreak,
    Manual
}
