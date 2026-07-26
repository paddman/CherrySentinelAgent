using CherrySentinel.Shared.Contracts;

namespace CherrySentinel.Core.Abstractions;

public interface ITransportClient
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
    Task<IngestResponse?> SendBatchAsync(AgentIngestBatch batch, CancellationToken cancellationToken);
    Task SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken);
}
