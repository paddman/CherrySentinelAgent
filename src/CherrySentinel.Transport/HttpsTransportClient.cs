using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CherrySentinel.Core.Abstractions;
using CherrySentinel.Core.Configuration;
using CherrySentinel.Core.Reliability;
using CherrySentinel.Shared.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CherrySentinel.Transport;

public sealed class HttpsTransportClient : ITransportClient, IDisposable
{
    private readonly CentralServerOptions _options;
    private readonly ILogger<HttpsTransportClient> _logger;
    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly ExponentialBackoff _backoff = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HttpsTransportClient(
        IOptions<CentralServerOptions> options,
        ILogger<HttpsTransportClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _handler = CreateHandler(_options);
        var baseUrl = string.IsNullOrWhiteSpace(_options.Url) ? "https://localhost:7443" : _options.Url;
        _http = new HttpClient(_handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.TimeoutSeconds))
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CherrySentinel-Agent/1.0");
    }

    private static HttpClientHandler CreateHandler(CentralServerOptions options)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };

        if (options.AllowUntrustedServerCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        if (options.EnableMtls && !string.IsNullOrWhiteSpace(options.ClientCertificatePath))
        {
            if (!File.Exists(options.ClientCertificatePath))
            {
                throw new FileNotFoundException("Client certificate not found", options.ClientCertificatePath);
            }

            var cert = string.IsNullOrEmpty(options.ClientCertificatePassword)
                ? X509CertificateLoader.LoadPkcs12FromFile(options.ClientCertificatePath, null)
                : X509CertificateLoader.LoadPkcs12FromFile(options.ClientCertificatePath, options.ClientCertificatePassword);
            handler.ClientCertificates.Add(cert);
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;
        }

        return handler;
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync("health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _backoff.Reset();
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Central server unreachable; next delay ~{Delay}", _backoff.NextDelay());
            return false;
        }
    }

    public async Task<IngestResponse?> SendBatchAsync(AgentIngestBatch batch, CancellationToken cancellationToken)
    {
        try
        {
            batch.IdempotencyKey ??= Guid.NewGuid().ToString("N");
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/ingest")
            {
                Content = CreateJsonContent(batch)
            };
            request.Headers.TryAddWithoutValidation("Idempotency-Key", batch.IdempotencyKey);

            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("Ingest failed: {Status} {Body}", response.StatusCode, body);
                return null;
            }

            _backoff.Reset();
            return await response.Content.ReadFromJsonAsync<IngestResponse>(JsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send ingest batch");
            return null;
        }
    }

    public async Task SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("api/v1/agents/heartbeat", heartbeat, JsonOptions, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Heartbeat failed: {Status}", response.StatusCode);
                return;
            }

            var hb = await response.Content.ReadFromJsonAsync<HeartbeatResponse>(JsonOptions, cancellationToken);
            if (hb is not null)
            {
                heartbeat.ClockSkewSeconds = hb.ClockSkewSeconds;
                if (ClockSkew.IsSignificant(TimeSpan.FromSeconds(hb.ClockSkewSeconds)))
                {
                    _logger.LogWarning("Clock skew vs server: {Seconds}s", hb.ClockSkewSeconds);
                }
            }

            _backoff.Reset();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Heartbeat error (offline mode continues)");
        }
    }

    private static ByteArrayContent CreateJsonContent<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        // Optional gzip compression for large batches
        if (json.Length > 4096)
        {
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                gz.Write(json, 0, json.Length);
            }

            var content = new ByteArrayContent(ms.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            content.Headers.ContentEncoding.Add("gzip");
            return content;
        }

        var plain = new ByteArrayContent(json);
        plain.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return plain;
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}
