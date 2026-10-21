using PilahDok.Core.Models;

namespace PilahDok.Core.Errors;

public class PilahDokException : Exception
{
    public PilahDokException(string publicCode, string publicMessage, int statusCode = 400, Exception? innerException = null)
        : base(publicMessage, innerException)
    {
        PublicCode = publicCode;
        PublicMessage = publicMessage;
        StatusCode = statusCode;
    }

    public string PublicCode { get; }
    public string PublicMessage { get; }
    public int StatusCode { get; }
}

public sealed class InvalidJobStateException : PilahDokException
{
    public InvalidJobStateException(JobStatus current, params JobStatus[] expected)
        : base(
            "invalid_job_state",
            $"Operasi tidak dapat dilakukan saat status job adalah {current.ToString().ToLowerInvariant()}.",
            409)
    {
        Expected = expected;
    }

    public IReadOnlyList<JobStatus> Expected { get; }
}
