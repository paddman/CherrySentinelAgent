using CherrySentinel.Agent;
using CherrySentinel.Collectors.Windows.Events;
using CherrySentinel.Collectors.Windows.Network;
using CherrySentinel.Collectors.Windows.Process;
using CherrySentinel.Collectors.Windows.Services;
using CherrySentinel.Collectors.Windows.Tasks;
using CherrySentinel.Core.Abstractions;
using CherrySentinel.Core.Configuration;
using CherrySentinel.Core.Identity;
using CherrySentinel.Detection;
using CherrySentinel.Response;
using CherrySentinel.Response.Evidence;
using CherrySentinel.Response.Firewall;
using CherrySentinel.Storage.Sqlite;
using CherrySentinel.Transport;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CherrySentinelAgent";
});

var logDir = builder.Configuration["LoggingPaths:Directory"]
             ?? @"C:\ProgramData\CherrySentinel\Agent\logs";
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(logDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        fileSizeLimitBytes: 50 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        shared: true)
    .CreateLogger();

builder.Services.AddSerilog();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<CentralServerOptions>(builder.Configuration.GetSection(CentralServerOptions.SectionName));
// back-compat alias
builder.Services.PostConfigure<CentralServerOptions>(o =>
{
    var legacy = builder.Configuration.GetSection("CentralServer");
    if (legacy.Exists() && string.IsNullOrWhiteSpace(o.Url))
    {
        o.Url = legacy["BaseUrl"] ?? legacy["Url"] ?? o.Url;
    }

    if (builder.Configuration.GetValue<int?>("Agent:HeartbeatSeconds") is int hb)
    {
        o.HeartbeatIntervalSeconds = hb;
    }
});
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<EventLogCollectorOptions>(builder.Configuration.GetSection("Collectors"));
builder.Services.Configure<NetworkCollectorOptions>(builder.Configuration.GetSection("Collectors"));
builder.Services.Configure<ProcessCollectorOptions>(builder.Configuration.GetSection("Collectors"));
builder.Services.Configure<ScheduledTaskCollectorOptions>(builder.Configuration.GetSection("Collectors"));
builder.Services.Configure<DetectionOptions>(builder.Configuration.GetSection(DetectionOptions.SectionName));
builder.Services.Configure<ResponseOptions>(builder.Configuration.GetSection(ResponseOptions.SectionName));

builder.Services.PostConfigure<AgentOptions>(opts =>
{
    if (string.IsNullOrWhiteSpace(opts.ComputerName))
    {
        opts.ComputerName = Environment.MachineName;
    }

    // Always report assembly product version (not stale appsettings 1.0.0)
    opts.Version = CherrySentinel.Shared.ProductInfo.GetVersion();

    AgentIdentity.EnsureAgentId(opts);
    Directory.CreateDirectory(opts.DataDirectory);
});

builder.Services.PostConfigure<NetworkCollectorOptions>(o =>
{
    var interval = builder.Configuration.GetValue<int?>("Agent:CollectionIntervalSeconds");
    if (interval is > 0)
    {
        o.PollIntervalSeconds = interval.Value;
    }
});

builder.Services.AddSingleton<ILocalStore, SqliteLocalStore>();
builder.Services.AddSingleton<ProcessEnricher>();
builder.Services.AddSingleton<IServiceResolver, WindowsServiceResolver>();
builder.Services.AddSingleton<IEventLogCollector, WindowsEventLogCollector>();
builder.Services.AddSingleton<INetworkCollector, IpHelperNetworkCollector>();
builder.Services.AddSingleton<IProcessCollector, WmiProcessCollector>();
builder.Services.AddSingleton<IScheduledTaskCollector, TaskSchedulerCollector>();
builder.Services.AddSingleton<IDetectionEngine, RuleEngine>();
builder.Services.AddSingleton<FirewallBlocker>();
builder.Services.AddSingleton<IEvidenceCollector, EvidencePackager>();
builder.Services.AddSingleton<RuntimePolicyState>();
builder.Services.AddSingleton<IResponseExecutor, LocalResponseExecutor>();
builder.Services.AddSingleton<ITransportClient, HttpsTransportClient>();
builder.Services.AddSingleton<CherrySentinel.Transport.SyslogForwarder>();
builder.Services.AddHostedService<AgentWorker>();

try
{
    Log.Information("Starting Cherry Sentinel Agent (DetectOnly default)");
    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
