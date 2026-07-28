using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using CherrySentinel.Shared;
using CherrySentinel.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

// Cherry Sentinel Linux Agent — heartbeat client for Central (collectors expand in later phases).
var contentRoot = AppContext.BaseDirectory;
var logDir = Directory.Exists("/var/log/cherrysentinel")
    ? "/var/log/cherrysentinel"
    : Path.Combine(contentRoot, "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var version = ProductInfo.GetVersion();
    Log.Information("Cherry Sentinel Linux Agent v{Version} starting…", version);

    var host = Host.CreateDefaultBuilder(args)
        .UseContentRoot(contentRoot)
        .UseSystemd()
        .UseSerilog()
        .ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.SetBasePath(contentRoot);
            cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            cfg.AddEnvironmentVariables(prefix: "CHERRY_");
            cfg.AddCommandLine(args);
        })
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
    Environment.ExitCode = 1;
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
        var url = (_config["Server:Url"] ?? "https://localhost:7443").Trim().TrimEnd('/');
        var allowUntrusted = _config.GetValue("Server:AllowUntrustedServerCertificate", true);
        var heartbeatSec = Math.Max(15, _config.GetValue("Server:HeartbeatIntervalSeconds", 60));
        var agentId = _config["Agent:AgentId"];
        if (string.IsNullOrWhiteSpace(agentId))
        {
            agentId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Environment.MachineName + "|linux|cherrysentinel"))).ToLowerInvariant()[..16];
            try
            {
                PersistAgentId(agentId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not persist AgentId");
            }
        }

        var version = ProductInfo.GetVersion() + "-linux";
        _logger.LogInformation(
            "Linux Agent v{Version} Host={Host} AgentId={Id} Central={Url}",
            version, Environment.MachineName, agentId, url);

        using var handler = new HttpClientHandler();
        if (allowUntrusted)
        {
            handler.ServerCertificateCustomValidationCallback =
                static (HttpRequestMessage _, X509Certificate2? _, X509Chain? _, SslPolicyErrors _) => true;
        }

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri(url + "/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CherrySentinel-Agent-Linux/{ProductInfo.GetVersion()}");

        // Optional register once
        try
        {
            var reg = new AgentRegistrationRequest
            {
                AgentId = agentId,
                ComputerName = Environment.MachineName,
                AgentVersion = version,
                OsVersion = Environment.OSVersion.ToString(),
                HostIp = TryGetIp()
            };
            using var regResp = await http.PostAsJsonAsync("api/v1/agents/register", reg, stoppingToken);
            _logger.LogInformation("Register → HTTP {Code}", (int)regResp.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Register failed (will still heartbeat)");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hb = new AgentHeartbeat
                {
                    AgentId = agentId,
                    ComputerName = Environment.MachineName,
                    AgentVersion = version,
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
                    _logger.LogInformation("Heartbeat OK → {Base} agent={Id}", http.BaseAddress, agentId);
                else
                    _logger.LogWarning("Heartbeat failed: HTTP {Status}", (int)resp.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Heartbeat error (will retry)");
            }

            await Task.Delay(TimeSpan.FromSeconds(heartbeatSec), stoppingToken);
        }
    }

    private static void PersistAgentId(string agentId)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path)) return;
        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        var agent = node["Agent"] as JsonObject ?? new JsonObject();
        agent["AgentId"] = agentId;
        if (string.IsNullOrWhiteSpace(agent["ComputerName"]?.GetValue<string>()))
            agent["ComputerName"] = Environment.MachineName;
        node["Agent"] = agent;
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? TryGetIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}
