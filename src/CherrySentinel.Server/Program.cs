using System.IO.Compression;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CherrySentinel.Server.Correlation;
using CherrySentinel.Server.Data;
using CherrySentinel.Server.Services;
using CherrySentinel.Shared.Contracts;
using CherrySentinel.Shared.Models;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var logDir = builder.Configuration["LoggingPaths:Directory"] ?? "logs";
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logDir, "server-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31)
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.Configure<PostgresOptions>(builder.Configuration.GetSection(PostgresOptions.SectionName));
builder.Services.Configure<CorrelationOptions>(builder.Configuration.GetSection(CorrelationOptions.SectionName));
builder.Services.AddSingleton<PostgresStore>();
builder.Services.AddSingleton<CrossHostCorrelator>();
builder.Services.AddSingleton<LateralMovementTracker>();
builder.Services.AddSingleton<IngestService>();
builder.Services.AddSingleton<ActionService>();

builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<GzipCompressionProvider>();
    o.Providers.Add<BrotliCompressionProvider>();
});
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = builder.Configuration.GetValue("Security:MaxRequestBodyBytes", 20 * 1024 * 1024);
    var port = builder.Configuration.GetValue("Kestrel:Port", 7443);
    var enableMtls = builder.Configuration.GetValue("Security:EnableMtls", false);
    options.ListenAnyIP(port, listen =>
    {
        listen.UseHttps(https =>
        {
            if (enableMtls)
            {
                https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            }
        });
    });
});

var enableMtlsAuth = builder.Configuration.GetValue("Security:EnableMtls", false);
if (enableMtlsAuth)
{
    builder.Services.AddAuthentication(CertificateAuthenticationDefaults.AuthenticationScheme)
        .AddCertificate(options =>
        {
            options.AllowedCertificateTypes = CertificateTypes.All;
            options.RevocationMode = X509RevocationMode.NoCheck;
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();
app.UseResponseCompression();
app.Use(async (ctx, next) =>
{
    // Structured audit for mutating calls
    if (HttpMethods.IsPost(ctx.Request.Method))
    {
        Log.Information("AUDIT {Method} {Path} from {IP} len={Len}",
            ctx.Request.Method,
            ctx.Request.Path,
            ctx.Connection.RemoteIpAddress,
            ctx.Request.ContentLength);
    }

    await next();
});

using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<PostgresStore>();
    await store.InitializeAsync();
}

if (enableMtlsAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    product = "Cherry Sentinel Server",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }));

app.MapPost("/api/v1/agents/register", async (AgentRegistrationRequest req, PostgresStore store) =>
{
    await store.RegisterAgentAsync(req);
    return Results.Ok(new AgentRegistrationResponse
    {
        Accepted = true,
        Message = "registered",
        ServerUtc = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/v1/agents/heartbeat", async (AgentHeartbeat hb, PostgresStore store, ActionService actions) =>
{
    await store.UpsertAgentAsync(hb);
    var serverUtc = DateTimeOffset.UtcNow;
    var skew = (hb.TimestampUtc - serverUtc).TotalSeconds;
    var pending = await actions.GetPendingForAgentAsync(hb.AgentId);
    return Results.Ok(new HeartbeatResponse
    {
        Accepted = true,
        ServerUtc = serverUtc,
        ClockSkewSeconds = skew,
        PendingActions = pending
    });
});

// Back-compat
app.MapPost("/api/v1/heartbeat", async (AgentHeartbeat hb, PostgresStore store) =>
{
    await store.UpsertAgentAsync(hb);
    return Results.Ok(new { accepted = true, serverUtc = DateTimeOffset.UtcNow });
});

app.MapPost("/api/v1/events/batch", async (EventsBatchRequest req, IngestService ingest, HttpRequest http) =>
{
    var idem = req.IdempotencyKey ?? http.Headers["Idempotency-Key"].FirstOrDefault();
    var batch = new AgentIngestBatch
    {
        AgentId = req.AgentId,
        ComputerName = req.ComputerName,
        IdempotencyKey = idem,
        SecurityEvents = req.Events
    };
    return Results.Ok(await ingest.IngestAsync(batch, CancellationToken.None));
});

app.MapPost("/api/v1/connections/batch", async (ConnectionsBatchRequest req, IngestService ingest, HttpRequest http) =>
{
    var idem = req.IdempotencyKey ?? http.Headers["Idempotency-Key"].FirstOrDefault();
    var batch = new AgentIngestBatch
    {
        AgentId = req.AgentId,
        ComputerName = req.ComputerName,
        IdempotencyKey = idem,
        NetworkConnections = req.Connections
    };
    return Results.Ok(await ingest.IngestAsync(batch, CancellationToken.None));
});

app.MapPost("/api/v1/ingest", async (AgentIngestBatch batch, IngestService ingest, HttpRequest http) =>
{
    batch.IdempotencyKey ??= http.Headers["Idempotency-Key"].FirstOrDefault();
    return Results.Ok(await ingest.IngestAsync(batch, CancellationToken.None));
});

app.MapPost("/api/v1/incidents", async (Incident incident, PostgresStore store) =>
{
    await store.UpsertIncidentAsync(incident);
    Log.Warning("Incident upserted:\n{Display}", incident.FormatDisplay());
    return Results.Ok(incident);
});

app.MapGet("/api/v1/incidents", async (PostgresStore store, int take = 100) =>
{
    var incidents = await store.ListIncidentsAsync(Math.Clamp(take, 1, 500));
    return Results.Ok(incidents);
});

app.MapGet("/api/v1/incidents/{id}", async (string id, PostgresStore store) =>
{
    var incident = await store.GetIncidentAsync(id);
    return incident is null ? Results.NotFound() : Results.Ok(incident);
});

app.MapPost("/api/v1/actions", async (ResponseActionRequest request, ActionService actions) =>
{
    var saved = await actions.EnqueueAsync(request);
    return Results.Ok(saved);
});

app.MapGet("/api/v1/actions/{id}", async (string id, ActionService actions) =>
{
    var action = await actions.GetAsync(id);
    return action is null ? Results.NotFound() : Results.Ok(action);
});

app.MapGet("/api/v1/agents", async (PostgresStore store) => Results.Ok(await store.ListAgentsAsync()));

// Threat catalog + multi-host lateral tracking (detect/track only)
app.MapGet("/api/v1/threats/catalog", (LateralMovementTracker tracker) =>
    Results.Ok(tracker.GetThreatCatalog()));

app.MapGet("/api/v1/threats", (LateralMovementTracker tracker, int take = 100) =>
    Results.Ok(tracker.ListCampaigns(take)));

app.MapGet("/api/v1/threats/{id}", (string id, LateralMovementTracker tracker) =>
{
    var c = tracker.GetCampaign(id);
    return c is null ? Results.NotFound() : Results.Ok(c);
});

app.MapGet("/api/v1/threats/{id}/path", (string id, LateralMovementTracker tracker) =>
{
    var c = tracker.GetCampaign(id);
    if (c is null)
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        campaignId = c.CampaignId,
        display = c.FormatDisplay(),
        hops = c.Hops,
        hosts = c.InvolvedHosts,
        ips = c.InvolvedIps,
        users = c.InvolvedUsernames,
        categories = c.ThreatCategories
    });
});

app.MapGet("/api/v1/threats/by-host/{hostOrIp}", (string hostOrIp, LateralMovementTracker tracker) =>
    Results.Ok(tracker.FindByHostOrIp(hostOrIp)));

Log.Information("Cherry Sentinel Server starting");
app.Run();

public partial class Program;
