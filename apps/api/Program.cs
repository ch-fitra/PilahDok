using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using PilahDok.Api.Contracts;
using PilahDok.Api.Endpoints;
using PilahDok.Api.Errors;
using PilahDok.Api.Security;
using PilahDok.Core.Abstractions;
using PilahDok.Infrastructure;
using PilahDok.Infrastructure.Configuration;
using PilahDok.Infrastructure.Health;
using PilahDok.Infrastructure.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});
builder.Services.AddPilahDokInfrastructure(builder.Configuration);
builder.Services.AddSingleton<JobAuthorization>();
builder.Services.AddSingleton<PilahDokMetrics>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 1);

var configured = builder.Configuration.GetSection(PilahDokOptions.SectionName).Get<PilahDokOptions>() ?? new PilahDokOptions();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = configured.MaxUploadBytes + 1_024);
builder.Services.AddRequestTimeouts(options =>
{
    options.DefaultPolicy = new RequestTimeoutPolicy
    {
        Timeout = TimeSpan.FromSeconds(configured.RequestTimeoutSeconds),
        TimeoutStatusCode = StatusCodes.Status408RequestTimeout
    };
    options.AddPolicy("upload", TimeSpan.FromSeconds(configured.UploadTimeoutSeconds));
});
builder.Services.AddCors(options => options.AddPolicy("web", policy =>
    policy.WithOrigins(configured.AllowedOrigins)
        .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
        .WithHeaders("Content-Type", "X-Job-Token", "X-Download-Token", "X-File-Name")
        .SetPreflightMaxAge(TimeSpan.FromHours(1))));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/problem+json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            type = "https://pilahdok.local/problems/rate_limit_exceeded",
            title = "rate_limit_exceeded",
            status = 429,
            detail = "Terlalu banyak permintaan. Tunggu sebentar lalu coba lagi.",
            correlationId = context.HttpContext.TraceIdentifier
        }, cancellationToken);
    };
    options.AddPolicy("api", context => FixedWindow(context, configured.RateLimitPerMinute));
    options.AddPolicy("status", context => FixedWindow(context, Math.Max(120, configured.RateLimitPerMinute)));
    options.AddPolicy("upload", context => FixedWindow(context, Math.Max(5, configured.RateLimitPerMinute / 3)));
    options.AddPolicy("download", context => FixedWindow(context, configured.RateLimitPerMinute));
});
builder.Services.AddHealthChecks()
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck("storage", () =>
    {
        try
        {
            Directory.CreateDirectory(configured.StorageRoot);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Temporary storage is unavailable.", exception);
        }
    }, tags: ["ready"]);

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.TraceIdentifier = System.Diagnostics.ActivityTraceId.CreateRandom().ToString();
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    if (context.Request.Path.StartsWithSegments("/api/v1/jobs"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next();
});
app.UseCors("web");
app.UseRequestTimeouts();
app.UseRateLimiter();

app.MapOpenApi("/openapi/{documentName}.json");
app.MapJobEndpoints();
app.MapGet("/api/v1/capabilities", (
    IOptions<PilahDokOptions> options,
    IWordPaginationEngine pagination) =>
{
    var value = options.Value;
    return Results.Ok(new CapabilitiesResponse(
        ["docx"],
        ["heading1", "heading2", "chapter", "section", "pageBreak", "manual"],
        value.MaxUploadBytes,
        value.JobTtlMinutes,
        new PaginationCapability(
            pagination.IsAvailable && value.VisualPaginationEnabled,
            pagination.ProviderName,
            "DOCX tidak memiliki halaman visual tetap. Provider layout belum dikonfigurasi; pemisahan struktural tetap tersedia."),
        !value.AntivirusMode.Equals("disabled", StringComparison.OrdinalIgnoreCase)));
}).RequireRateLimiting("api").WithTags("Capabilities");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

app.Run();

static RateLimitPartition<string> FixedWindow(HttpContext context, int permitLimit)
{
    var key = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit,
        QueueLimit = 0,
        Window = TimeSpan.FromMinutes(1),
        AutoReplenishment = true
    });
}

public partial class Program;
