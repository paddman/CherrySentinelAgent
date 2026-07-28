using CherrySentinel.Shared.Models;

namespace CherrySentinel.Shared.Contracts;

public sealed class AgentRegistrationRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string? CertificateThumbprint { get; set; }
    public string? HostIp { get; set; }
}

public sealed class AgentRegistrationResponse
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset ServerUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AgentHeartbeat
{
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public long LocalQueueDepth { get; set; }
    public long DatabaseSizeBytes { get; set; }
    public string Status { get; set; } = "Healthy";
    public long WorkingSetBytes { get; set; }
    public double? CpuPercentEstimate { get; set; }
    public double ClockSkewSeconds { get; set; }

    /// <summary>Primary host IPv4/IPv6 for inventory (fleet UI).</summary>
    public string? HostIp { get; set; }

    /// <summary>Central URL this agent is configured to use (helps debug wrong-server config).</summary>
    public string? CentralUrl { get; set; }

    /// <summary>windows | linux | unknown</summary>
    public string Platform { get; set; } = "windows";

    /// <summary>Last outbound error (ingest/heartbeat), if any.</summary>
    public string? LastError { get; set; }
}

/// <summary>Fleet inventory row returned by GET /api/v1/agents</summary>
public sealed class AgentInventoryItem
{
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string? AgentVersion { get; set; }
    public string? OsVersion { get; set; }
    public string? HostIp { get; set; }
    public string? CentralUrl { get; set; }
    public string Platform { get; set; } = "windows";
    public DateTimeOffset LastSeenUtc { get; set; }
    public string? Status { get; set; }
    public long QueueDepth { get; set; }
    public string? LastError { get; set; }
    public bool Online { get; set; }
    public int OfflineSeconds { get; set; }
}

public sealed class HeartbeatResponse
{
    public bool Accepted { get; set; }
    public DateTimeOffset ServerUtc { get; set; } = DateTimeOffset.UtcNow;
    public double ClockSkewSeconds { get; set; }
    public List<ResponseActionRequest> PendingActions { get; set; } = [];
}

public sealed class AgentIngestBatch
{
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string AgentVersion { get; set; } = string.Empty;
    public DateTimeOffset SentAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? IdempotencyKey { get; set; }
    public List<SecurityEventRecord> SecurityEvents { get; set; } = [];
    public List<NetworkConnectionRecord> NetworkConnections { get; set; } = [];
    public List<ProcessRecord> Processes { get; set; } = [];
    public List<ServiceRecord> Services { get; set; } = [];
    public List<ScheduledTaskRecord> ScheduledTasks { get; set; } = [];
    public List<DetectionAlert> Alerts { get; set; } = [];
}

public sealed class IngestResponse
{
    public bool Accepted { get; set; }
    public string? Message { get; set; }
    public int ReceivedCount { get; set; }
    public List<string> CreatedIncidentIds { get; set; } = [];
    public bool Duplicate { get; set; }
}

public sealed class EventsBatchRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public List<SecurityEventRecord> Events { get; set; } = [];
}

public sealed class ConnectionsBatchRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = string.Empty;
    public string? IdempotencyKey { get; set; }
    public List<NetworkConnectionRecord> Connections { get; set; } = [];
}
