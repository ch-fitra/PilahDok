using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using PilahDok.Infrastructure;
using PilahDok.Infrastructure.Health;
using PilahDok.Infrastructure.Observability;
using PilahDok.Worker.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPilahDokInfrastructure(builder.Configuration);
builder.Services.AddSingleton<PilahDokMetrics>();
builder.Services.AddHostedService<JobWorkerService>();
builder.Services.AddHostedService<CleanupService>();
builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);

var app = builder.Build();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.Run();
