using CherrySentinel.Shared.Contracts;

namespace CherrySentinel.Core.Abstractions;

public interface ITransportClient
{
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
    Task<IngestResponse?> SendBatchAsync(AgentIngestBatch batch, CancellationToken cancellationToken);
    /// <summary>Returns heartbeat response including pending approved actions from Central.</summary>
    Task<HeartbeatResponse?> SendHeartbeatAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken);
}
