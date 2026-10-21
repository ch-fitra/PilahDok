using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using PilahDok.Core.Errors;

namespace PilahDok.Api.Errors;

public sealed partial class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var known = exception as PilahDokException;
        var status = known?.StatusCode ?? StatusCodes.Status500InternalServerError;
        var code = known?.PublicCode ?? "internal_error";
        var detail = known?.PublicMessage ?? "Terjadi kesalahan internal. Silakan coba lagi.";
        var correlationId = httpContext.TraceIdentifier;

        LogRequestFailure(
            logger,
            exception.GetType().Name,
            code,
            correlationId);
        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = code,
                Detail = detail,
                Type = $"https://pilahdok.local/problems/{code}",
                Extensions = { ["correlationId"] = correlationId }
            },
            Exception = exception
        });
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Error, Message = "Request failed with type {ExceptionType}, code {PublicCode}, correlation {CorrelationId}")]
    private static partial void LogRequestFailure(ILogger logger, string exceptionType, string publicCode, string correlationId);
}
