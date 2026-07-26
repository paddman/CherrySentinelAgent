using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using CherrySentinel.Shared.Models;

namespace CherrySentinel.Dashboard.Services;

public sealed class CentralApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public string BaseUrl { get; private set; }

    public CentralApiClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CherrySentinel-Dashboard/1.0");
    }

    public void SetBaseUrl(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http.BaseAddress = new Uri(BaseUrl + "/");
    }

    public async Task<bool> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("health", ct);
            if (resp.IsSuccessStatusCode) return true;
            using var resp2 = await _http.GetAsync("api/v1/health", ct);
            return resp2.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<Incident>> GetIncidentsAsync(int take = 100, CancellationToken ct = default)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<Incident>>($"api/v1/incidents?take={take}", JsonOptions, ct);
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<ThreatCampaign>> GetThreatsAsync(int take = 100, CancellationToken ct = default)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<ThreatCampaign>>($"api/v1/threats?take={take}", JsonOptions, ct);
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<ThreatCatalogEntry>> GetCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<ThreatCatalogEntry>>("api/v1/threats/catalog", JsonOptions, ct);
            return list ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<List<object>> GetAgentsAsync(CancellationToken ct = default)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<JsonElement>>("api/v1/agents", JsonOptions, ct);
            return list?.Cast<object>().ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<ThreatCampaign?> GetThreatAsync(string id, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ThreatCampaign>($"api/v1/threats/{id}", JsonOptions, ct);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
