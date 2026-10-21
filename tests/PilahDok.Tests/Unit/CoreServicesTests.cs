using PilahDok.Core.Errors;
using PilahDok.Core.Models;
using PilahDok.Core.Services;

namespace PilahDok.Tests.Unit;

public sealed class CoreServicesTests
{
    [Theory]
    [InlineData("../../rahasia.docx", "rahasia.docx")]
    [InlineData("BAB I: Pendahuluan?.docx", "BAB-I-Pendahuluan.docx")]
    [InlineData("CON.docx", "file-CON.docx")]
    [InlineData("   .docx", "bagian.docx")]
    public void SanitizeFilenameRemovesUnsafeContent(string input, string expected)
    {
        Assert.Equal(expected, SafeFileName.Sanitize(input));
    }

    [Fact]
    public void MakeUniqueUsesDeterministicSuffixes()
    {
        var result = SafeFileName.MakeUnique(["BAB I.docx", "BAB I.docx", "bab i.docx"]);
        Assert.Equal(["BAB-I.docx", "BAB-I-2.docx", "bab-i-3.docx"], result);
    }

    [Fact]
    public void OutputNamesAlwaysUseDocxEvenWhenRenamedToAnotherExtension()
    {
        Assert.Equal(["chapter.docx", "chapter-2.docx"], SafeFileName.MakeUnique(["chapter.exe", "chapter.pdf"]));
    }

    [Fact]
    public void NormalizeTitleCollapsesWhitespaceAndLimitsLength()
    {
        var result = SafeFileName.NormalizeTitle("  BAB\tI\r\n" + new string('A', 200));
        Assert.Equal(160, result.Length);
        Assert.StartsWith("BAB I A", result, StringComparison.Ordinal);
    }

    [Fact]
    public void SecureTokenRoundTripsWithoutStoringRawToken()
    {
        var token = SecureToken.Generate();
        var hash = SecureToken.Hash(token);
        Assert.True(SecureToken.Matches(token, hash));
        Assert.False(SecureToken.Matches(token + "x", hash));
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
    }

    [Fact]
    public void StateMachineRejectsInvalidTransition()
    {
        var job = CreateJob(JobStatus.Created);
        Assert.Throws<InvalidJobStateException>(() =>
            JobStateMachine.Transition(job, JobStatus.Completed, DateTimeOffset.UtcNow, 100, "completed"));
    }

    [Fact]
    public void StateMachineAllowsExpectedLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        var job = CreateJob(JobStatus.Created);
        job = JobStateMachine.Transition(job, JobStatus.Uploading, now, 5, "uploading");
        job = JobStateMachine.Transition(job, JobStatus.Validating, now, 70, "validating");
        job = JobStateMachine.Transition(job, JobStatus.Uploaded, now, 100, "uploaded");
        job = JobStateMachine.Transition(job, JobStatus.Queued, now, 10, "queued");
        job = JobStateMachine.Transition(job, JobStatus.Analyzing, now, 30, "analyzing");
        job = JobStateMachine.Transition(job, JobStatus.Ready, now, 100, "ready");
        Assert.Equal(JobStatus.Ready, job.Status);
    }

    [Fact]
    public void TtlIsCalculatedFromCreationTime()
    {
        var created = new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero);
        var expires = created.AddMinutes(60);
        Assert.Equal(TimeSpan.FromHours(1), expires - created);
    }

    private static JobRecord CreateJob(JobStatus status) => new()
    {
        JobId = new string('a', 32),
        AccessTokenHash = new string('b', 64),
        DownloadTokenHash = new string('c', 64),
        Status = status,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        InternalCorrelationId = "test"
    };
}
