using System.Net.Http.Json;
using CherrySentinel.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// Skeleton Linux agent — heartbeat to Central. Full collectors land in Phase 4.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("/var/log/cherrysentinel/agent-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

try
{
    var host = Host.CreateDefaultBuilder(args)
        .UseSystemd()
        .UseSerilog()
        .ConfigureServices(services =>
        {
            services.AddHostedService<LinuxAgentWorker>();
        })
        .Build();

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Linux agent terminated");
}
finally
{
    await Log.CloseAndFlushAsync();
}

internal sealed class LinuxAgentWorker : BackgroundService
{
    private readonly ILogger<LinuxAgentWorker> _logger;
    private readonly IConfiguration _config;

    public LinuxAgentWorker(ILogger<LinuxAgentWorker> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var url = _config["Server:Url"] ?? "https://localhost:7443";
        var agentId = _config["Agent:AgentId"];
        if (string.IsNullOrWhiteSpace(agentId))
        {
            agentId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "|linux"))).ToLowerInvariant()[..16];
        }

        _logger.LogInformation("Cherry Sentinel Linux Agent starting. Host={Host} Central={Url} AgentId={Id}",
            Environment.MachineName, url, agentId);

        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(url.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hb = new AgentHeartbeat
                {
                    AgentId = agentId,
                    ComputerName = Environment.MachineName,
                    AgentVersion = CherrySentinel.Shared.ProductInfo.GetVersion() + "-linux",
                    OsVersion = Environment.OSVersion.ToString(),
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Status = "Healthy",
                    Platform = "linux",
                    CentralUrl = url,
                    HostIp = TryGetIp(),
                    LocalQueueDepth = 0
                };

                using var resp = await http.PostAsJsonAsync("api/v1/agents/heartbeat", hb, stoppingToken);
                if (resp.IsSuccessStatusCode)
                    _logger.LogInformation("Heartbeat OK → {Base}", http.BaseAddress);
                else
                    _logger.LogWarning("Heartbeat failed: {Status}", resp.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Heartbeat error (will retry)");
            }

            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
        }
    }

    private static string? TryGetIp()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(Environment.MachineName)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
