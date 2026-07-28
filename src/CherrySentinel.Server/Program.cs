using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;
using CherrySentinel.Server.Correlation;
using CherrySentinel.Server.Data;
using CherrySentinel.Server.Services;
using CherrySentinel.Server.Signatures;
using CherrySentinel.Server.Syslog;
using CherrySentinel.Shared.Contracts;
using CherrySentinel.Shared.Models;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Allow running as Windows Service (Central install)
builder.Host.UseWindowsService(o => o.ServiceName = "CherrySentinelCentral");

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
builder.Services.Configure<SqliteCentralOptions>(builder.Configuration.GetSection(SqliteCentralOptions.SectionName));
builder.Services.Configure<CorrelationOptions>(builder.Configuration.GetSection(CorrelationOptions.SectionName));
builder.Services.Configure<SyslogOptions>(builder.Configuration.GetSection(SyslogOptions.SectionName));
builder.Services.AddSingleton<OpenSourceSignatureEngine>();
builder.Services.AddHostedService<SyslogListenerService>();

// Default: SQLite (works out of the box). Set Database:Provider=Postgres for PostgreSQL.
var dbProvider = builder.Configuration["Database:Provider"] ?? "Sqlite";
if (string.Equals(dbProvider, "Postgres", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(dbProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ICentralStore, PostgresStore>();
    Log.Information("Central database provider: PostgreSQL");
}
else
{
    builder.Services.AddSingleton<ICentralStore, SqliteCentralStore>();
    Log.Information("Central database provider: SQLite (lab/default)");
}

builder.Services.AddSingleton<CrossHostCorrelator>();
builder.Services.AddSingleton<LateralMovementTracker>();
builder.Services.AddSingleton<IngestService>();
builder.Services.AddSingleton<ActionService>();

// Agent may send gzip-compressed ingest batches (body > ~4KB). Without this, ASP.NET returns 400 BadRequest.
builder.Services.AddRequestDecompression();
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
    var certPath = builder.Configuration["Security:CertificatePath"]
                   ?? @"C:\ProgramData\CherrySentinel\Server\certs\central.pfx";
    var certPassword = builder.Configuration["Security:CertificatePassword"] ?? "CherrySentinel!";
    var serverCert = EnsureServerCertificate(certPath, certPassword);
    Log.Information("HTTPS certificate: {Subject} thumbprint={Thumb}", serverCert.Subject, serverCert.Thumbprint);

    options.ListenAnyIP(port, listen =>
    {
        listen.UseHttps(https =>
        {
            https.ServerCertificate = serverCert;
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
// Must run before model binding so gzip/br request bodies become readable JSON
app.UseRequestDecompression();
app.UseResponseCompression();
app.Use(async (ctx, next) =>
{
    // Structured audit for mutating calls
    if (HttpMethods.IsPost(ctx.Request.Method))
    {
        Log.Information("AUDIT {Method} {Path} from {IP} len={Len} enc={Enc}",
            ctx.Request.Method,
            ctx.Request.Path,
            ctx.Connection.RemoteIpAddress,
            ctx.Request.ContentLength,
            ctx.Request.Headers.ContentEncoding.ToString());
    }

    await next();

    // Surface model-binding failures that look like "Agent never talks to Central"
    if (HttpMethods.IsPost(ctx.Request.Method) &&
        ctx.Response.StatusCode == StatusCodes.Status400BadRequest &&
        ctx.Request.Path.StartsWithSegments("/api"))
    {
        Log.Warning("API 400 BadRequest path={Path} enc={Enc} len={Len} (often gzip without request decompression, or JSON schema mismatch)",
            ctx.Request.Path,
            ctx.Request.Headers.ContentEncoding.ToString(),
            ctx.Request.ContentLength);
    }
});

using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<ICentralStore>();
    await store.InitializeAsync();
    var tracker = scope.ServiceProvider.GetRequiredService<LateralMovementTracker>();
    await tracker.LoadAsync();
}

if (enableMtlsAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

var centralVersion = CherrySentinel.Shared.ProductInfo.GetVersion();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    product = "Cherry Sentinel Central",
    version = centralVersion,
    productVersion = centralVersion,
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/api/v1/health", (IOptions<SyslogOptions> syslog, OpenSourceSignatureEngine sigs) => Results.Ok(new
{
    status = "ok",
    product = "Cherry Sentinel Central",
    version = centralVersion,
    productVersion = centralVersion,
    utc = DateTimeOffset.UtcNow,
    syslog = new
    {
        enabled = syslog.Value.Enabled,
        udpPort = syslog.Value.UdpPort,
        signatures = sigs.Signatures.Count
    }
}));

app.MapGet("/api/v1/signatures", (OpenSourceSignatureEngine sigs) =>
    Results.Ok(sigs.Signatures.Select(s => new
    {
        s.Id,
        s.Name,
        s.Severity,
        s.Category,
        s.MitreTechnique,
        s.Source,
        s.Enabled
    })));

app.MapPost("/api/v1/agents/register", async (AgentRegistrationRequest req, ICentralStore store) =>
{
    await store.RegisterAgentAsync(req);
    return Results.Ok(new AgentRegistrationResponse
    {
        Accepted = true,
        Message = "registered",
        ServerUtc = DateTimeOffset.UtcNow
    });
});

app.MapPost("/api/v1/agents/heartbeat", async (AgentHeartbeat hb, ICentralStore store, ActionService actions) =>
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
app.MapPost("/api/v1/heartbeat", async (AgentHeartbeat hb, ICentralStore store) =>
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

app.MapPost("/api/v1/ingest", async (AgentIngestBatch? batch, IngestService ingest, HttpRequest http) =>
{
    if (batch is null)
    {
        Log.Warning("Ingest body null/unbound — Content-Encoding={Enc} Content-Type={Ct} Length={Len}",
            http.Headers.ContentEncoding.ToString(),
            http.ContentType,
            http.ContentLength);
        return Results.BadRequest(new { error = "invalid_ingest_body", hint = "Enable request decompression for gzip; check JSON schema" });
    }

    batch.IdempotencyKey ??= http.Headers["Idempotency-Key"].FirstOrDefault();
    var result = await ingest.IngestAsync(batch, CancellationToken.None);
    Log.Information(
        "Ingest accepted={Ok} agent={AgentId} events={E} conn={C} proc={P} alerts={A} incidents={I}",
        result.Accepted,
        batch.AgentId,
        batch.SecurityEvents.Count,
        batch.NetworkConnections.Count,
        batch.Processes.Count,
        batch.Alerts.Count,
        result.CreatedIncidentIds.Count);
    return Results.Ok(result);
});

app.MapPost("/api/v1/incidents", async (Incident incident, ICentralStore store) =>
{
    await store.UpsertIncidentAsync(incident);
    Log.Warning("Incident upserted:\n{Display}", incident.FormatDisplay());
    return Results.Ok(incident);
});

app.MapGet("/api/v1/incidents", async (ICentralStore store, int take = 100) =>
{
    var incidents = await store.ListIncidentsAsync(Math.Clamp(take, 1, 500));
    return Results.Ok(incidents);
});

app.MapGet("/api/v1/incidents/{id}", async (string id, ICentralStore store) =>
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

app.MapGet("/api/v1/agents", async (ICentralStore store) => Results.Ok(await store.ListAgentsAsync()));

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

/// <summary>
/// Create or load a durable self-signed HTTPS cert for Windows Service / production lab use
/// (dev-certs are per-user and unavailable under LocalSystem).
/// </summary>
static X509Certificate2 EnsureServerCertificate(string pfxPath, string password)
{
    try
    {
        var dir = Path.GetDirectoryName(pfxPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(pfxPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password);
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=CherrySentinelCentral",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        // SAN: localhost + machine name
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        req.CertificateExtensions.Add(san.Build());

        var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        var exportable = new X509Certificate2(
            cert.Export(X509ContentType.Pfx, password),
            password,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
        File.WriteAllBytes(pfxPath, exportable.Export(X509ContentType.Pfx, password));
        Log.Information("Created self-signed HTTPS certificate at {Path}", pfxPath);
        return exportable;
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to create/load HTTPS certificate at {Path}", pfxPath);
        throw;
    }
}

public partial class Program;
