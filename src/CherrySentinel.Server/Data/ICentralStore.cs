using CherrySentinel.Shared.Contracts;
using CherrySentinel.Shared.Models;

namespace CherrySentinel.Server.Data;

public interface ICentralStore
{
    Task InitializeAsync();
    Task RegisterAgentAsync(AgentRegistrationRequest req);
    Task UpsertAgentAsync(AgentHeartbeat hb);
    Task<bool> HasIdempotencyKeyAsync(string key);
    Task SaveIdempotencyKeyAsync(string key);
    Task SaveBatchAsync(AgentIngestBatch batch);
    Task UpsertIncidentAsync(Incident incident);
    Task<IReadOnlyList<Incident>> ListIncidentsAsync(int take);
    Task<Incident?> GetIncidentAsync(string id);
    Task<IReadOnlyList<object>> ListAgentsAsync();
    Task<IReadOnlyList<NetworkConnectionRecord>> FindOutboundAsync(
        string remoteIp,
        int? remotePort,
        DateTimeOffset from,
        DateTimeOffset to);

    // Durable pending actions (survive Central restart)
    Task SavePendingActionAsync(ResponseActionRequest request, string agentKey);
    Task<List<ResponseActionRequest>> TakePendingActionsAsync(string agentId);
    Task<ResponseActionRequest?> GetPendingActionAsync(string requestId);

    // Durable threat campaigns (JSON blob)
    Task UpsertCampaignJsonAsync(string campaignId, string json);
    Task<IReadOnlyList<(string Id, string Json)>> ListCampaignJsonAsync(int take);
}
