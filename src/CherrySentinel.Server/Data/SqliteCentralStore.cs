using System.Text.Json;
using CherrySentinel.Shared.Contracts;
using CherrySentinel.Shared.Enums;
using CherrySentinel.Shared.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace CherrySentinel.Server.Data;

public sealed class SqliteCentralOptions
{
    public const string SectionName = "Sqlite";
    public string DatabasePath { get; set; } =
        @"C:\ProgramData\CherrySentinel\Server\central.db";
}

/// <summary>
/// Default Central store for lab / single-server installs (no PostgreSQL required).
/// </summary>
public sealed class SqliteCentralStore : ICentralStore
{
    private readonly string _dbPath;
    private readonly ILogger<SqliteCentralStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public SqliteCentralStore(IOptions<SqliteCentralOptions> options, ILogger<SqliteCentralStore> logger)
    {
        _dbPath = options.Value.DatabasePath;
        _logger = logger;
    }

    private SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS agents (
                    agent_id TEXT PRIMARY KEY,
                    computer_name TEXT NOT NULL,
                    agent_version TEXT,
                    os_version TEXT,
                    host_ip TEXT,
                    certificate_thumbprint TEXT,
                    last_seen_utc TEXT NOT NULL,
                    queue_depth INTEGER,
                    db_size_bytes INTEGER,
                    status TEXT,
                    working_set_bytes INTEGER,
                    clock_skew_seconds REAL
                );
                CREATE TABLE IF NOT EXISTS idempotency_keys (
                    key TEXT PRIMARY KEY,
                    created_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS security_events (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id TEXT NOT NULL,
                    computer_name TEXT NOT NULL,
                    event_id INTEGER NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    username TEXT,
                    domain TEXT,
                    source_ip TEXT,
                    source_port INTEGER,
                    destination_ip TEXT,
                    destination_port INTEGER,
                    logon_type INTEGER,
                    process_id INTEGER,
                    process_path TEXT,
                    status TEXT,
                    raw_xml TEXT,
                    event_record_id INTEGER,
                    payload TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_sec_events_ts ON security_events(timestamp_utc);
                CREATE TABLE IF NOT EXISTS network_connections (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_id TEXT NOT NULL,
                    computer_name TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    protocol TEXT,
                    local_address TEXT,
                    local_port INTEGER,
                    remote_address TEXT,
                    remote_port INTEGER,
                    process_id INTEGER,
                    process_name TEXT,
                    process_path TEXT,
                    process_command_line TEXT,
                    service_names TEXT,
                    is_new INTEGER,
                    is_closed INTEGER,
                    payload TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_net_remote ON network_connections(remote_address, timestamp_utc);
                CREATE TABLE IF NOT EXISTS detection_alerts (
                    alert_id TEXT PRIMARY KEY,
                    agent_id TEXT,
                    computer_name TEXT,
                    rule_id TEXT,
                    severity INTEGER,
                    timestamp_utc TEXT,
                    source_ip TEXT,
                    destination_ip TEXT,
                    username TEXT,
                    payload TEXT
                );
                CREATE TABLE IF NOT EXISTS incidents (
                    incident_id TEXT PRIMARY KEY,
                    first_seen_utc TEXT,
                    last_seen_utc TEXT,
                    severity INTEGER,
                    title TEXT,
                    description TEXT,
                    correlation_key TEXT,
                    source_host TEXT,
                    source_agent_id TEXT,
                    source_ip TEXT,
                    source_port INTEGER,
                    source_process_id INTEGER,
                    source_process_name TEXT,
                    source_process_path TEXT,
                    source_service_names TEXT,
                    source_command_line TEXT,
                    destination_host TEXT,
                    destination_agent_id TEXT,
                    destination_ip TEXT,
                    destination_port INTEGER,
                    username TEXT,
                    domain TEXT,
                    logon_type INTEGER,
                    failed_logon_count INTEGER,
                    successful_logon_count INTEGER,
                    privileged_logon INTEGER,
                    evidence_json TEXT,
                    status TEXT
                );
                CREATE INDEX IF NOT EXISTS ix_incidents_last ON incidents(last_seen_utc);
                CREATE TABLE IF NOT EXISTS pending_actions (
                    request_id TEXT PRIMARY KEY,
                    agent_key TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    delivered INTEGER NOT NULL DEFAULT 0
                );
                CREATE INDEX IF NOT EXISTS ix_pending_agent ON pending_actions(agent_key, delivered);
                CREATE TABLE IF NOT EXISTS threat_campaigns (
                    campaign_id TEXT PRIMARY KEY,
                    payload TEXT NOT NULL,
                    last_seen_utc TEXT NOT NULL
                );
                """;
            await cmd.ExecuteNonQueryAsync();

            // Best-effort schema upgrades for existing DBs
            foreach (var alter in new[]
                     {
                         "ALTER TABLE agents ADD COLUMN central_url TEXT;",
                         "ALTER TABLE agents ADD COLUMN platform TEXT;",
                         "ALTER TABLE agents ADD COLUMN last_error TEXT;"
                     })
            {
                try
                {
                    await using var alt = conn.CreateCommand();
                    alt.CommandText = alter;
                    await alt.ExecuteNonQueryAsync();
                }
                catch
                {
                    // column already exists
                }
            }

            _logger.LogInformation("SQLite Central store ready at {Path}", _dbPath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RegisterAgentAsync(AgentRegistrationRequest req)
    {
        await UpsertAgentCoreAsync(req.AgentId, req.ComputerName, req.AgentVersion, req.OsVersion,
            queue: 0, db: 0, status: "Registered", ws: 0, skew: 0, hostIp: req.HostIp, thumb: req.CertificateThumbprint);
    }

    public async Task UpsertAgentAsync(AgentHeartbeat hb)
    {
        await UpsertAgentCoreAsync(hb.AgentId, hb.ComputerName, hb.AgentVersion, hb.OsVersion,
            hb.LocalQueueDepth, hb.DatabaseSizeBytes, hb.Status, hb.WorkingSetBytes, hb.ClockSkewSeconds,
            hostIp: hb.HostIp, thumb: null, ts: hb.TimestampUtc,
            centralUrl: hb.CentralUrl, platform: hb.Platform, lastError: hb.LastError);
    }

    private async Task UpsertAgentCoreAsync(
        string agentId, string computerName, string? version, string? os,
        long queue, long db, string status, long ws, double skew,
        string? hostIp, string? thumb, DateTimeOffset? ts = null,
        string? centralUrl = null, string? platform = null, string? lastError = null)
    {
        var when = (ts ?? DateTimeOffset.UtcNow).ToString("O");
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO agents(agent_id, computer_name, agent_version, os_version, host_ip, certificate_thumbprint,
                    last_seen_utc, queue_depth, db_size_bytes, status, working_set_bytes, clock_skew_seconds,
                    central_url, platform, last_error)
                VALUES ($id, $cn, $ver, $os, $ip, $thumb, $ts, $q, $db, $st, $ws, $skew, $curl, $plat, $err)
                ON CONFLICT(agent_id) DO UPDATE SET
                    computer_name=excluded.computer_name,
                    agent_version=excluded.agent_version,
                    os_version=excluded.os_version,
                    host_ip=COALESCE(excluded.host_ip, agents.host_ip),
                    certificate_thumbprint=COALESCE(excluded.certificate_thumbprint, agents.certificate_thumbprint),
                    last_seen_utc=excluded.last_seen_utc,
                    queue_depth=excluded.queue_depth,
                    db_size_bytes=excluded.db_size_bytes,
                    status=excluded.status,
                    working_set_bytes=excluded.working_set_bytes,
                    clock_skew_seconds=excluded.clock_skew_seconds,
                    central_url=COALESCE(excluded.central_url, agents.central_url),
                    platform=COALESCE(excluded.platform, agents.platform),
                    last_error=excluded.last_error;
                """;
            cmd.Parameters.AddWithValue("$id", agentId);
            cmd.Parameters.AddWithValue("$cn", computerName);
            cmd.Parameters.AddWithValue("$ver", (object?)version ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$os", (object?)os ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ip", (object?)hostIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$thumb", (object?)thumb ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ts", when);
            cmd.Parameters.AddWithValue("$q", queue);
            cmd.Parameters.AddWithValue("$db", db);
            cmd.Parameters.AddWithValue("$st", status);
            cmd.Parameters.AddWithValue("$ws", ws);
            cmd.Parameters.AddWithValue("$skew", skew);
            cmd.Parameters.AddWithValue("$curl", (object?)centralUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$plat", (object?)platform ?? "windows");
            cmd.Parameters.AddWithValue("$err", (object?)lastError ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> HasIdempotencyKeyAsync(string key)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM idempotency_keys WHERE key=$k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    public async Task SaveIdempotencyKeyAsync(string key)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO idempotency_keys(key, created_at_utc) VALUES ($k, $t);";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SaveBatchAsync(AgentIngestBatch batch)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var tx = conn.BeginTransaction();

            foreach (var e in batch.SecurityEvents)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO security_events(
                        agent_id, computer_name, event_id, timestamp_utc, username, domain,
                        source_ip, source_port, destination_ip, destination_port, logon_type,
                        process_id, process_path, status, raw_xml, event_record_id, payload)
                    VALUES (
                        $agent_id, $computer_name, $event_id, $timestamp_utc, $username, $domain,
                        $source_ip, $source_port, $destination_ip, $destination_port, $logon_type,
                        $process_id, $process_path, $status, $raw_xml, $event_record_id, $payload);
                    """;
                cmd.Parameters.AddWithValue("$agent_id", e.AgentId);
                cmd.Parameters.AddWithValue("$computer_name", e.ComputerName);
                cmd.Parameters.AddWithValue("$event_id", e.EventId);
                cmd.Parameters.AddWithValue("$timestamp_utc", e.TimestampUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$username", (object?)e.Username ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$domain", (object?)e.Domain ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$source_ip", (object?)e.SourceIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$source_port", (object?)e.SourcePort ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$destination_ip", (object?)e.DestinationIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$destination_port", (object?)e.DestinationPort ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$logon_type", (object?)e.LogonType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$process_id", (object?)e.ProcessId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$process_path", (object?)e.ProcessPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$status", (object?)e.Status ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$raw_xml", (object?)e.RawXml ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$event_record_id", e.EventRecordId);
                cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(e, JsonOptions));
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var n in batch.NetworkConnections)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT INTO network_connections(
                        agent_id, computer_name, timestamp_utc, protocol, local_address, local_port,
                        remote_address, remote_port, process_id, process_name, process_path,
                        process_command_line, service_names, is_new, is_closed, payload)
                    VALUES (
                        $agent_id, $computer_name, $timestamp_utc, $protocol, $local_address, $local_port,
                        $remote_address, $remote_port, $process_id, $process_name, $process_path,
                        $process_command_line, $service_names, $is_new, $is_closed, $payload);
                    """;
                cmd.Parameters.AddWithValue("$agent_id", n.AgentId);
                cmd.Parameters.AddWithValue("$computer_name", n.ComputerName);
                cmd.Parameters.AddWithValue("$timestamp_utc", n.TimestampUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$protocol", n.Protocol);
                cmd.Parameters.AddWithValue("$local_address", n.LocalAddress);
                cmd.Parameters.AddWithValue("$local_port", n.LocalPort);
                cmd.Parameters.AddWithValue("$remote_address", n.RemoteAddress);
                cmd.Parameters.AddWithValue("$remote_port", n.RemotePort);
                cmd.Parameters.AddWithValue("$process_id", n.ProcessId);
                cmd.Parameters.AddWithValue("$process_name", (object?)n.ProcessName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$process_path", (object?)n.ProcessPath ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$process_command_line", (object?)n.ProcessCommandLine ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$service_names", (object?)n.ServiceNames ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$is_new", n.IsNew ? 1 : 0);
                cmd.Parameters.AddWithValue("$is_closed", n.IsClosed ? 1 : 0);
                cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(n, JsonOptions));
                await cmd.ExecuteNonQueryAsync();
            }

            foreach (var a in batch.Alerts)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    """
                    INSERT OR IGNORE INTO detection_alerts(
                        alert_id, agent_id, computer_name, rule_id, severity, timestamp_utc,
                        source_ip, destination_ip, username, payload)
                    VALUES ($alert_id, $agent_id, $computer_name, $rule_id, $severity, $timestamp_utc,
                        $source_ip, $destination_ip, $username, $payload);
                    """;
                cmd.Parameters.AddWithValue("$alert_id", a.AlertId);
                cmd.Parameters.AddWithValue("$agent_id", a.AgentId);
                cmd.Parameters.AddWithValue("$computer_name", a.ComputerName);
                cmd.Parameters.AddWithValue("$rule_id", a.RuleId);
                cmd.Parameters.AddWithValue("$severity", (int)a.Severity);
                cmd.Parameters.AddWithValue("$timestamp_utc", a.TimestampUtc.ToString("O"));
                cmd.Parameters.AddWithValue("$source_ip", (object?)a.SourceIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$destination_ip", (object?)a.DestinationIp ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$username", (object?)a.Username ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(a, JsonOptions));
                await cmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertIncidentAsync(Incident incident)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO incidents(
                    incident_id, first_seen_utc, last_seen_utc, severity, title, description, correlation_key,
                    source_host, source_agent_id, source_ip, source_port, source_process_id, source_process_name,
                    source_process_path, source_service_names, source_command_line,
                    destination_host, destination_agent_id, destination_ip, destination_port,
                    username, domain, logon_type, failed_logon_count, successful_logon_count, privileged_logon,
                    evidence_json, status)
                VALUES (
                    $incident_id, $first_seen_utc, $last_seen_utc, $severity, $title, $description, $correlation_key,
                    $source_host, $source_agent_id, $source_ip, $source_port, $source_process_id, $source_process_name,
                    $source_process_path, $source_service_names, $source_command_line,
                    $destination_host, $destination_agent_id, $destination_ip, $destination_port,
                    $username, $domain, $logon_type, $failed_logon_count, $successful_logon_count, $privileged_logon,
                    $evidence_json, $status)
                ON CONFLICT(incident_id) DO UPDATE SET
                    last_seen_utc=excluded.last_seen_utc,
                    severity=excluded.severity,
                    title=excluded.title,
                    description=excluded.description,
                    evidence_json=excluded.evidence_json,
                    status=excluded.status,
                    failed_logon_count=excluded.failed_logon_count,
                    successful_logon_count=excluded.successful_logon_count;
                """;
            cmd.Parameters.AddWithValue("$incident_id", incident.IncidentId);
            cmd.Parameters.AddWithValue("$first_seen_utc", incident.FirstSeenUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$last_seen_utc", incident.LastSeenUtc.ToString("O"));
            cmd.Parameters.AddWithValue("$severity", (int)incident.Severity);
            cmd.Parameters.AddWithValue("$title", incident.Title ?? "");
            cmd.Parameters.AddWithValue("$description", incident.Description ?? "");
            cmd.Parameters.AddWithValue("$correlation_key", incident.CorrelationKey ?? "");
            cmd.Parameters.AddWithValue("$source_host", (object?)incident.SourceHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_agent_id", (object?)incident.SourceAgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_ip", (object?)incident.SourceIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_port", (object?)incident.SourcePort ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_process_id", (object?)incident.SourceProcessId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_process_name", (object?)incident.SourceProcessName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_process_path", (object?)incident.SourceProcessPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_service_names", (object?)incident.SourceServiceNames ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source_command_line", (object?)incident.SourceCommandLine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$destination_host", (object?)incident.DestinationHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$destination_agent_id", (object?)incident.DestinationAgentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$destination_ip", (object?)incident.DestinationIp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$destination_port", (object?)incident.DestinationPort ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$username", (object?)incident.Username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$domain", (object?)incident.Domain ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$logon_type", (object?)incident.LogonType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$failed_logon_count", incident.FailedLogonCount);
            cmd.Parameters.AddWithValue("$successful_logon_count", incident.SuccessfulLogonCount);
            cmd.Parameters.AddWithValue("$privileged_logon", incident.PrivilegedLogon ? 1 : 0);
            cmd.Parameters.AddWithValue("$evidence_json",
                string.IsNullOrWhiteSpace(incident.EvidenceJson) ? "[]" : incident.EvidenceJson);
            cmd.Parameters.AddWithValue("$status", incident.Status ?? "Open");
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<Incident>> ListIncidentsAsync(int take)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT incident_id, first_seen_utc, last_seen_utc, severity, title, description, correlation_key,
                   source_host, source_agent_id, source_ip, source_port, source_process_id, source_process_name,
                   source_process_path, source_service_names, source_command_line,
                   destination_host, destination_agent_id, destination_ip, destination_port,
                   username, domain, logon_type, failed_logon_count, successful_logon_count, privileged_logon,
                   evidence_json, status
            FROM incidents ORDER BY last_seen_utc DESC LIMIT $take;
            """;
        cmd.Parameters.AddWithValue("$take", take);
        var list = new List<Incident>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(ReadIncident(reader));
        }

        return list;
    }

    public async Task<Incident?> GetIncidentAsync(string id)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT incident_id, first_seen_utc, last_seen_utc, severity, title, description, correlation_key,
                   source_host, source_agent_id, source_ip, source_port, source_process_id, source_process_name,
                   source_process_path, source_service_names, source_command_line,
                   destination_host, destination_agent_id, destination_ip, destination_port,
                   username, domain, logon_type, failed_logon_count, successful_logon_count, privileged_logon,
                   evidence_json, status
            FROM incidents WHERE incident_id=$id LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return ReadIncident(reader);
    }

    public async Task<IReadOnlyList<object>> ListAgentsAsync()
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT agent_id, computer_name, agent_version, os_version, last_seen_utc, status, queue_depth,
                   host_ip, central_url, platform, last_error
            FROM agents ORDER BY last_seen_utc DESC;
            """;
        var list = new List<object>();
        var now = DateTimeOffset.UtcNow;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var lastSeen = DateTimeOffset.Parse(reader.GetString(4));
            var offlineSec = (int)Math.Max(0, (now - lastSeen).TotalSeconds);
            var online = offlineSec <= 120;
            list.Add(new AgentInventoryItem
            {
                AgentId = reader.GetString(0),
                ComputerName = reader.GetString(1),
                AgentVersion = reader.IsDBNull(2) ? null : reader.GetString(2),
                OsVersion = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastSeenUtc = lastSeen,
                Status = online ? (reader.IsDBNull(5) ? "Healthy" : reader.GetString(5)) : "Offline",
                QueueDepth = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                HostIp = reader.FieldCount > 7 && !reader.IsDBNull(7) ? reader.GetString(7) : null,
                CentralUrl = reader.FieldCount > 8 && !reader.IsDBNull(8) ? reader.GetString(8) : null,
                Platform = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : "windows",
                LastError = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
                Online = online,
                OfflineSeconds = offlineSec
            });
        }

        return list;
    }

    public async Task SavePendingActionAsync(ResponseActionRequest request, string agentKey)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO pending_actions(request_id, agent_key, payload, created_at_utc, delivered)
                VALUES ($id, $agent, $payload, $ts, 0)
                ON CONFLICT(request_id) DO UPDATE SET
                    agent_key=excluded.agent_key,
                    payload=excluded.payload,
                    delivered=0;
                """;
            cmd.Parameters.AddWithValue("$id", request.RequestId);
            cmd.Parameters.AddWithValue("$agent", agentKey);
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(request, JsonOptions));
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<ResponseActionRequest>> TakePendingActionsAsync(string agentId)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            // Prefer agent-specific queue; include broadcast once (marked delivered for that row only if agent_key=broadcast — rare)
            cmd.CommandText =
                """
                SELECT request_id, payload, agent_key FROM pending_actions
                WHERE delivered=0 AND (agent_key=$a OR agent_key='broadcast')
                ORDER BY created_at_utc ASC
                LIMIT 50;
                """;
            cmd.Parameters.AddWithValue("$a", agentId);
            var rows = new List<(string Id, string Payload, string AgentKey)>();
            await using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            var result = new List<ResponseActionRequest>();
            foreach (var (id, payload, agentKey) in rows)
            {
                var req = JsonSerializer.Deserialize<ResponseActionRequest>(payload, JsonOptions);
                if (req is null) continue;
                result.Add(req);

                // Consume agent-specific actions. Leave true broadcast for other agents (not deleted).
                if (!string.Equals(agentKey, "broadcast", StringComparison.OrdinalIgnoreCase))
                {
                    await using var mark = conn.CreateCommand();
                    mark.CommandText = "UPDATE pending_actions SET delivered=1 WHERE request_id=$id;";
                    mark.Parameters.AddWithValue("$id", id);
                    await mark.ExecuteNonQueryAsync();
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ResponseActionRequest?> GetPendingActionAsync(string requestId)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload FROM pending_actions WHERE request_id=$id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", requestId);
        var o = await cmd.ExecuteScalarAsync();
        if (o is not string json) return null;
        return JsonSerializer.Deserialize<ResponseActionRequest>(json, JsonOptions);
    }

    public async Task UpsertCampaignJsonAsync(string campaignId, string json)
    {
        await _gate.WaitAsync();
        try
        {
            await using var conn = Open();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO threat_campaigns(campaign_id, payload, last_seen_utc)
                VALUES ($id, $p, $ts)
                ON CONFLICT(campaign_id) DO UPDATE SET payload=excluded.payload, last_seen_utc=excluded.last_seen_utc;
                """;
            cmd.Parameters.AddWithValue("$id", campaignId);
            cmd.Parameters.AddWithValue("$p", json);
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<(string Id, string Json)>> ListCampaignJsonAsync(int take)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT campaign_id, payload FROM threat_campaigns
            ORDER BY last_seen_utc DESC LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", Math.Clamp(take, 1, 500));
        var list = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            list.Add((reader.GetString(0), reader.GetString(1)));
        return list;
    }

    public async Task<IReadOnlyList<NetworkConnectionRecord>> FindOutboundAsync(
        string remoteIp, int? remotePort, DateTimeOffset from, DateTimeOffset to)
    {
        await using var conn = Open();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT agent_id, computer_name, timestamp_utc, local_address, local_port, remote_address, remote_port,
                   process_id, process_name, process_path, process_command_line, service_names
            FROM network_connections
            WHERE remote_address=$remote
              AND timestamp_utc BETWEEN $from AND $to
              AND ($port IS NULL OR remote_port=$port)
            ORDER BY timestamp_utc DESC
            LIMIT 200;
            """;
        cmd.Parameters.AddWithValue("$remote", remoteIp);
        cmd.Parameters.AddWithValue("$from", from.ToString("O"));
        cmd.Parameters.AddWithValue("$to", to.ToString("O"));
        cmd.Parameters.AddWithValue("$port", (object?)remotePort ?? DBNull.Value);
        var list = new List<NetworkConnectionRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(new NetworkConnectionRecord
            {
                AgentId = reader.GetString(0),
                ComputerName = reader.GetString(1),
                TimestampUtc = DateTimeOffset.Parse(reader.GetString(2)),
                LocalAddress = reader.IsDBNull(3) ? "" : reader.GetString(3),
                LocalPort = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                RemoteAddress = reader.IsDBNull(5) ? "" : reader.GetString(5),
                RemotePort = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                ProcessId = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                ProcessName = reader.IsDBNull(8) ? null : reader.GetString(8),
                ProcessPath = reader.IsDBNull(9) ? null : reader.GetString(9),
                ProcessCommandLine = reader.IsDBNull(10) ? null : reader.GetString(10),
                ServiceNames = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }

        return list;
    }

    private static Incident ReadIncident(SqliteDataReader reader)
    {
        var i = new Incident
        {
            IncidentId = reader.GetString(0),
            FirstSeenUtc = DateTimeOffset.Parse(reader.GetString(1)),
            LastSeenUtc = DateTimeOffset.Parse(reader.GetString(2)),
            Severity = (Severity)reader.GetInt32(3),
            Title = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Description = reader.IsDBNull(5) ? "" : reader.GetString(5),
            CorrelationKey = reader.IsDBNull(6) ? "" : reader.GetString(6),
            SourceHost = reader.IsDBNull(7) ? null : reader.GetString(7),
            SourceAgentId = reader.IsDBNull(8) ? null : reader.GetString(8),
            SourceIp = reader.IsDBNull(9) ? null : reader.GetString(9),
            SourcePort = reader.IsDBNull(10) ? null : reader.GetInt32(10),
            SourceProcessId = reader.IsDBNull(11) ? null : reader.GetInt32(11),
            SourceProcessName = reader.IsDBNull(12) ? null : reader.GetString(12),
            SourceProcessPath = reader.IsDBNull(13) ? null : reader.GetString(13),
            SourceServiceNames = reader.IsDBNull(14) ? null : reader.GetString(14),
            SourceCommandLine = reader.IsDBNull(15) ? null : reader.GetString(15),
            DestinationHost = reader.IsDBNull(16) ? null : reader.GetString(16),
            DestinationAgentId = reader.IsDBNull(17) ? null : reader.GetString(17),
            DestinationIp = reader.IsDBNull(18) ? null : reader.GetString(18),
            DestinationPort = reader.IsDBNull(19) ? null : reader.GetInt32(19),
            Username = reader.IsDBNull(20) ? null : reader.GetString(20),
            Domain = reader.IsDBNull(21) ? null : reader.GetString(21),
            LogonType = reader.IsDBNull(22) ? null : reader.GetInt32(22),
            FailedLogonCount = reader.IsDBNull(23) ? 0 : reader.GetInt32(23),
            SuccessfulLogonCount = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
            PrivilegedLogon = !reader.IsDBNull(25) && reader.GetInt32(25) == 1,
            Status = reader.IsDBNull(27) ? "Open" : reader.GetString(27)
        };
        return i;
    }
}
