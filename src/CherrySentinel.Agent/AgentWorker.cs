using System.Text.Json;
using CherrySentinel.Core.Abstractions;
using CherrySentinel.Core.Compatibility;
using CherrySentinel.Core.Configuration;
using CherrySentinel.Core.Identity;
using CherrySentinel.Shared.Contracts;
using CherrySentinel.Shared.Enums;
using CherrySentinel.Shared.Models;
using Microsoft.Extensions.Options;

namespace CherrySentinel.Agent;

public sealed class AgentWorker : BackgroundService
{
    private static readonly JsonSerializerOptions QueueJson = new(JsonSerializerDefaults.Web);
    private readonly ILogger<AgentWorker> _logger;
    private readonly AgentOptions _agentOptions;
    private readonly NetworkCollectorOptions _networkOptions;
    private readonly ProcessCollectorOptions _processOptions;
    private readonly ScheduledTaskCollectorOptions _taskOptions;
    private readonly DetectionOptions _detectionOptions;
    private readonly ResponseOptions _responseOptions;
    private readonly CentralServerOptions _centralOptions;
    private readonly StorageOptions _storageOptions;
    private readonly ILocalStore _store;
    private readonly IEventLogCollector _eventLogCollector;
    private readonly INetworkCollector _networkCollector;
    private readonly IProcessCollector _processCollector;
    private readonly IScheduledTaskCollector _taskCollector;
    private readonly IDetectionEngine _detectionEngine;
    private readonly IResponseExecutor _responseExecutor;
    private readonly ITransportClient _transport;
    private readonly CherrySentinel.Transport.SyslogForwarder _syslog;

    private readonly List<SecurityEventRecord> _eventBuffer = [];
    private readonly List<NetworkConnectionRecord> _connectionBuffer = [];
    private readonly object _bufferSync = new();
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private long _eventsCollected;
    private long _connectionsCollected;
    private long _processSnapshots;
    private long _taskChanges;
    private long _alertsRaised;
    private string? _lastAlertTitle;
    private DateTimeOffset? _lastAlertUtc;
    private DateTimeOffset? _lastActivityUtc;
    private bool _centralReachable;

    public AgentWorker(
        ILogger<AgentWorker> logger,
        IOptions<AgentOptions> agentOptions,
        IOptions<NetworkCollectorOptions> networkOptions,
        IOptions<ProcessCollectorOptions> processOptions,
        IOptions<ScheduledTaskCollectorOptions> taskOptions,
        IOptions<DetectionOptions> detectionOptions,
        IOptions<ResponseOptions> responseOptions,
        IOptions<CentralServerOptions> centralOptions,
        IOptions<StorageOptions> storageOptions,
        ILocalStore store,
        IEventLogCollector eventLogCollector,
        INetworkCollector networkCollector,
        IProcessCollector processCollector,
        IScheduledTaskCollector taskCollector,
        IDetectionEngine detectionEngine,
        IResponseExecutor responseExecutor,
        ITransportClient transport,
        CherrySentinel.Transport.SyslogForwarder syslog)
    {
        _logger = logger;
        _agentOptions = agentOptions.Value;
        _networkOptions = networkOptions.Value;
        _processOptions = processOptions.Value;
        _taskOptions = taskOptions.Value;
        _detectionOptions = detectionOptions.Value;
        _responseOptions = responseOptions.Value;
        _centralOptions = centralOptions.Value;
        _storageOptions = storageOptions.Value;
        _store = store;
        _eventLogCollector = eventLogCollector;
        _networkCollector = networkCollector;
        _processCollector = processCollector;
        _taskCollector = taskCollector;
        _detectionEngine = detectionEngine;
        _responseExecutor = responseExecutor;
        _transport = transport;
        _syslog = syslog;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var report = WindowsCompatibility.BuildReport();
        _logger.LogInformation("Compatibility: {Report}", JsonSerializer.Serialize(report));
        if (!report.MeetsMinimum)
        {
            _logger.LogCritical("OS does not meet Windows Server 2012 minimum. Agent will idle.");
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        AgentIdentity.EnsureAgentId(_agentOptions);
        Directory.CreateDirectory(_agentOptions.DataDirectory);

        await _store.InitializeAsync(stoppingToken);
        await _detectionEngine.InitializeAsync(stoppingToken);

        _eventLogCollector.EventReceived += OnEventReceived;
        await _eventLogCollector.StartAsync(stoppingToken);
        var mode = ResolveSecurityMode();
        _logger.LogInformation(
            "Cherry Sentinel Agent {Version} started. Mode={Mode} AgentId={AgentId} Central={Central}",
            _agentOptions.Version, mode, _agentOptions.AgentId, _centralOptions.Url);

        // Yield before each loop body so one hung collector cannot block sibling loops from starting.
        // Status + flush + heartbeat must always schedule even if network/detect freezes.
        var statusTask = RunLoopAsync("status", TimeSpan.FromSeconds(5), WriteStatusFileAsync, stoppingToken);
        var flushTask = RunLoopAsync("flush", TimeSpan.FromSeconds(Math.Max(5, _centralOptions.FlushIntervalSeconds)), FlushOutboundAsync, stoppingToken);
        var heartbeatTask = RunLoopAsync("heartbeat", TimeSpan.FromSeconds(Math.Max(15, _centralOptions.HeartbeatIntervalSeconds)), HeartbeatAsync, stoppingToken);
        var networkTask = RunLoopAsync("network", TimeSpan.FromSeconds(Math.Max(2, _networkOptions.PollIntervalSeconds)), CollectNetworkAsync, stoppingToken);
        var processTask = RunLoopAsync("process", TimeSpan.FromSeconds(Math.Max(10, _processOptions.PollIntervalSeconds)), CollectProcessAsync, stoppingToken);
        var taskTask = RunLoopAsync("tasks", TimeSpan.FromSeconds(Math.Max(30, _taskOptions.PollIntervalSeconds)), CollectTasksAsync, stoppingToken);
        var detectTask = RunLoopAsync("detect", TimeSpan.FromSeconds(Math.Max(5, _detectionOptions.EvaluationIntervalSeconds)), EvaluateDetectionsAsync, stoppingToken);
        var maintTask = RunLoopAsync("maintenance", TimeSpan.FromMinutes(Math.Max(5, _storageOptions.MaintenanceIntervalMinutes)), () => _store.RunMaintenanceAsync(stoppingToken), stoppingToken);

        // Immediate central probe + first heartbeat so Dashboard sees this host quickly (non-blocking for loops).
        _ = Task.Run(async () =>
        {
            try
            {
                _centralReachable = await _transport.IsReachableAsync(stoppingToken);
                _logger.LogInformation("Central reachable={Reachable} url={Url}", _centralReachable, _centralOptions.Url);
                if (_centralReachable)
                {
                    await HeartbeatAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial central contact failed");
            }

            try { await WriteStatusFileAsync(); } catch { /* ignore */ }
        }, stoppingToken);

        try
        {
            await Task.WhenAll(networkTask, processTask, taskTask, detectTask, flushTask, heartbeatTask, maintTask, statusTask);
        }
        finally
        {
            _eventLogCollector.EventReceived -= OnEventReceived;
            await _eventLogCollector.StopAsync(CancellationToken.None);
            await WriteStatusFileAsync(stopping: true);
        }
    }

    private void OnEventReceived(object? sender, SecurityEventRecord e)
    {
        lock (_bufferSync)
        {
            _eventBuffer.Add(e);
        }

        Interlocked.Increment(ref _eventsCollected);
        _lastActivityUtc = DateTimeOffset.UtcNow;
    }

    private async Task CollectNetworkAsync()
    {
        if (!_networkOptions.Enabled) return;
        var diffs = await _networkCollector.CollectDiffAsync(CancellationToken.None);
        if (diffs.Count == 0) return;
        await _store.SaveNetworkConnectionsAsync(diffs, CancellationToken.None);
        Interlocked.Add(ref _connectionsCollected, diffs.Count);
        _lastActivityUtc = DateTimeOffset.UtcNow;
        lock (_bufferSync)
        {
            _connectionBuffer.AddRange(diffs);
            if (_connectionBuffer.Count > 5000)
            {
                _connectionBuffer.RemoveRange(0, _connectionBuffer.Count - 2500);
            }
        }
    }

    private async Task CollectProcessAsync()
    {
        if (!_processOptions.Enabled) return;
        var processes = await _processCollector.CollectAsync(CancellationToken.None);
        await _store.SaveProcessesAsync(processes, CancellationToken.None);
        Interlocked.Add(ref _processSnapshots, processes.Count);
        _lastActivityUtc = DateTimeOffset.UtcNow;
    }

    private async Task CollectTasksAsync()
    {
        if (!_taskOptions.Enabled) return;
        var changes = await _taskCollector.CollectChangesAsync(CancellationToken.None);
        await _store.SaveScheduledTasksAsync(changes, CancellationToken.None);
        if (changes.Count > 0)
        {
            Interlocked.Add(ref _taskChanges, changes.Count);
            _lastActivityUtc = DateTimeOffset.UtcNow;
        }
    }

    private async Task EvaluateDetectionsAsync()
    {
        List<SecurityEventRecord> events;
        List<NetworkConnectionRecord> connections;
        lock (_bufferSync)
        {
            events = _eventBuffer.ToList();
            _eventBuffer.Clear();
            connections = _connectionBuffer.ToList();
        }

        if (events.Count > 0)
        {
            await _store.SaveSecurityEventsAsync(events, CancellationToken.None);
        }

        if (!_detectionOptions.Enabled)
        {
            return;
        }

        // Include recent persisted events for sliding windows (bounded so DB scan cannot starve transport).
        var from = DateTimeOffset.UtcNow.AddMinutes(-10);
        var recent = await _store.QuerySecurityEventsAsync(from, DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None);
        var merged = recent
            .Concat(events)
            .GroupBy(e => e.EventRecordId + ":" + e.EventId + ":" + e.TimestampUtc.ToString("O"))
            .Select(g => g.First())
            .OrderByDescending(e => e.TimestampUtc)
            .Take(5000)
            .ToList();

        var alerts = await _detectionEngine.EvaluateAsync(merged, connections, CancellationToken.None);
        foreach (var alert in alerts.Where(a => !a.Suppressed))
        {
            await _store.SaveAlertAsync(alert, CancellationToken.None);
            var action = await _responseExecutor.ExecuteAsync(alert, CancellationToken.None);
            await _store.SaveResponseActionAsync(action, CancellationToken.None);
            Interlocked.Increment(ref _alertsRaised);
            _lastAlertTitle = alert.Title;
            _lastAlertUtc = DateTimeOffset.UtcNow;
            _lastActivityUtc = DateTimeOffset.UtcNow;
            _logger.LogWarning(
                "ALERT {Rule} {Severity}: {Title} → response={ActionType}/{Status} {Result}",
                alert.RuleId, alert.Severity, alert.Title,
                action.ActionType, action.Status, action.Result ?? action.Error);

            // Optional syslog → Central (or SIEM) on port 5514 by default
            try
            {
                await _syslog.SendAlertAsync(alert, _agentOptions.ComputerName, CancellationToken.None);
            }
            catch
            {
                // never break detection loop
            }
        }
    }

    private async Task FlushOutboundAsync()
    {
        // Offline-first: if central is down, queue remains local.
        _centralReachable = await _transport.IsReachableAsync(CancellationToken.None);
        if (!_centralReachable)
        {
            return;
        }

        // Keep batches modest so a giant backlog cannot freeze flush / starve status loops.
        var take = Math.Clamp(_centralOptions.BatchSize, 1, 50);
        var batchItems = await _store.DequeueOutboundBatchAsync(take, CancellationToken.None);
        if (batchItems.Count == 0)
        {
            return;
        }

        var batch = new AgentIngestBatch
        {
            AgentId = _agentOptions.AgentId,
            ComputerName = _agentOptions.ComputerName,
            AgentVersion = _agentOptions.Version,
            SentAtUtc = DateTimeOffset.UtcNow
        };

        var ids = new List<long>();
        foreach (var item in batchItems)
        {
            ids.Add(item.Id);
            try
            {
                switch (item.ItemType)
                {
                    case QueueItemType.SecurityEvent:
                        var ev = JsonSerializer.Deserialize<List<SecurityEventRecord>>(item.PayloadJson, QueueJson) ?? [];
                        batch.SecurityEvents.AddRange(ev);
                        break;
                    case QueueItemType.NetworkConnection:
                        var nc = JsonSerializer.Deserialize<List<NetworkConnectionRecord>>(item.PayloadJson, QueueJson) ?? [];
                        batch.NetworkConnections.AddRange(nc);
                        break;
                    case QueueItemType.ProcessSnapshot:
                        var pr = JsonSerializer.Deserialize<List<ProcessRecord>>(item.PayloadJson, QueueJson) ?? [];
                        batch.Processes.AddRange(pr);
                        break;
                    case QueueItemType.ServiceMap:
                        var sv = JsonSerializer.Deserialize<List<ServiceRecord>>(item.PayloadJson, QueueJson) ?? [];
                        batch.Services.AddRange(sv);
                        break;
                    case QueueItemType.ScheduledTask:
                        var tk = JsonSerializer.Deserialize<List<ScheduledTaskRecord>>(item.PayloadJson, QueueJson) ?? [];
                        batch.ScheduledTasks.AddRange(tk);
                        break;
                    case QueueItemType.DetectionAlert:
                        var al = JsonSerializer.Deserialize<DetectionAlert>(item.PayloadJson, QueueJson);
                        if (al is not null) batch.Alerts.Add(al);
                        break;
                }
            }
            catch (Exception ex)
            {
                await _store.MarkOutboundFailedAsync(item.Id, ex.Message, CancellationToken.None);
            }
        }

        using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, _centralOptions.TimeoutSeconds)));
        var response = await _transport.SendBatchAsync(batch, flushCts.Token);
        if (response is { Accepted: true })
        {
            _lastOutboundError = null;
            await _store.MarkOutboundSentAsync(ids, CancellationToken.None);
            _logger.LogInformation(
                "Ingest OK → Central events={E} conn={C} proc={P} alerts={A} queueItems={N}",
                batch.SecurityEvents.Count, batch.NetworkConnections.Count, batch.Processes.Count, batch.Alerts.Count, ids.Count);
            if (response.CreatedIncidentIds.Count > 0)
            {
                _logger.LogInformation("Central created incidents: {Ids}", string.Join(',', response.CreatedIncidentIds));
            }
        }
        else
        {
            _lastOutboundError = "ingest rejected or unreachable";
            foreach (var id in ids)
            {
                await _store.MarkOutboundFailedAsync(id, "ingest rejected or unreachable", CancellationToken.None);
            }
        }
    }

    private static string? TryGetPrimaryIpv4()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
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

    private string? _lastOutboundError;

    private async Task HeartbeatAsync()
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        var hb = new AgentHeartbeat
        {
            AgentId = _agentOptions.AgentId,
            ComputerName = _agentOptions.ComputerName,
            AgentVersion = _agentOptions.Version,
            OsVersion = WindowsCompatibility.OsVersion.ToString(),
            TimestampUtc = DateTimeOffset.UtcNow,
            LocalQueueDepth = await _store.GetOutboundQueueDepthAsync(CancellationToken.None),
            DatabaseSizeBytes = await _store.GetDatabaseSizeBytesAsync(CancellationToken.None),
            WorkingSetBytes = proc.WorkingSet64,
            Status = "Healthy",
            HostIp = TryGetPrimaryIpv4(),
            CentralUrl = _centralOptions.Url,
            Platform = "windows",
            LastError = _lastOutboundError
        };
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, _centralOptions.TimeoutSeconds))))
        {
            _centralReachable = await _transport.IsReachableAsync(cts.Token);
        }

        HeartbeatResponse? hbResp = null;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, _centralOptions.TimeoutSeconds))))
        {
            hbResp = await _transport.SendHeartbeatAsync(hb, cts.Token);
        }

        if (hbResp?.PendingActions is { Count: > 0 } actions)
        {
            foreach (var action in actions)
            {
                try
                {
                    _logger.LogWarning(
                        "Executing Central action {Type} approved={Approved} target={Ip}:{Port} id={Id}",
                        action.ActionType, action.Approved, action.TargetIp, action.TargetPort, action.RequestId);
                    // Central-approved actions are explicit operator intent — force Approved flag.
                    action.Approved = true;
                    action.ApprovalId ??= action.RequestId;
                    var result = await _responseExecutor.ExecuteRequestAsync(action, CancellationToken.None);
                    await _store.SaveResponseActionAsync(result, CancellationToken.None);
                    _logger.LogWarning("Action result {Type} status={Status} {Result}",
                        result.ActionType, result.Status, result.Result ?? result.Error);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed executing Central action {Type}", action.ActionType);
                }
            }
        }

        // Status loop owns status.json — never block heartbeat on status I/O.
    }

    private async Task WriteStatusFileAsync() => await WriteStatusFileAsync(stopping: false);

    private async Task WriteStatusFileAsync(bool stopping)
    {
        try
        {
            Directory.CreateDirectory(_agentOptions.DataDirectory);

            // Never call Central here. Keep DB reads best-effort with short timeout.
            long queueDepth = 0;
            long dbSize = 0;
            long workingSet = 0;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                queueDepth = await _store.GetOutboundQueueDepthAsync(cts.Token).WaitAsync(cts.Token);
            }
            catch { /* ignore */ }
            try { dbSize = await _store.GetDatabaseSizeBytesAsync(CancellationToken.None); } catch { /* ignore */ }
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                workingSet = proc.WorkingSet64;
            }
            catch { /* ignore */ }

            var payload = new Dictionary<string, object?>
            {
                ["UpdatedAtUtc"] = DateTimeOffset.UtcNow,
                ["StartedAtUtc"] = _startedAtUtc,
                ["State"] = stopping ? "Stopping" : "Monitoring",
                ["Message"] = stopping
                    ? "Agent is stopping"
                    : "Agent is actively monitoring this system",
                ["AgentId"] = _agentOptions.AgentId,
                ["ComputerName"] = _agentOptions.ComputerName,
                ["Version"] = _agentOptions.Version,
                ["DetectOnly"] = _agentOptions.DetectOnly,
                ["Mode"] = ResolveSecurityMode(),
                ["ResponseMode"] = _responseOptions.Mode,
                ["AutoBlockMinSeverity"] = _responseOptions.AutoBlockMinSeverity,
                ["CentralUrl"] = _centralOptions.Url,
                ["CentralReachable"] = _centralReachable,
                ["QueueDepth"] = queueDepth,
                ["DatabaseSizeBytes"] = dbSize,
                ["WorkingSetBytes"] = workingSet,
                ["EventsCollected"] = Interlocked.Read(ref _eventsCollected),
                ["ConnectionsCollected"] = Interlocked.Read(ref _connectionsCollected),
                ["ProcessSnapshots"] = Interlocked.Read(ref _processSnapshots),
                ["TaskChanges"] = Interlocked.Read(ref _taskChanges),
                ["AlertsRaised"] = Interlocked.Read(ref _alertsRaised),
                ["LastAlertTitle"] = _lastAlertTitle,
                ["LastAlertUtc"] = _lastAlertUtc,
                ["LastActivityUtc"] = _lastActivityUtc,
                ["Collectors"] = new Dictionary<string, bool>
                {
                    ["SecurityEvents"] = true,
                    ["NetworkConnections"] = _networkOptions.Enabled,
                    ["Processes"] = _processOptions.Enabled,
                    ["Services"] = _processOptions.Services,
                    ["ScheduledTasks"] = _taskOptions.Enabled,
                    ["Detection"] = _detectionOptions.Enabled
                }
            };

            var path = Path.Combine(_agentOptions.DataDirectory, "status.json");
            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            // Sync write is fine and avoids rare async file hangs under service accounts.
            File.WriteAllText(tmp, json);
            File.Copy(tmp, path, overwrite: true);
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write status.json");
        }

        await Task.CompletedTask;
    }

    private string ResolveSecurityMode()
    {
        if (!string.IsNullOrWhiteSpace(_responseOptions.Mode) &&
            _responseOptions.Mode.Equals("Ips", StringComparison.OrdinalIgnoreCase))
        {
            return "Ips";
        }

        if (!string.IsNullOrWhiteSpace(_agentOptions.Mode) &&
            _agentOptions.Mode.Equals("Ips", StringComparison.OrdinalIgnoreCase))
        {
            return "Ips";
        }

        if (!_agentOptions.DetectOnly && !_responseOptions.DetectOnly && !_responseOptions.LogOnlyMode)
        {
            return "Ips";
        }

        return "Ids";
    }

    private async Task RunLoopAsync(string name, TimeSpan interval, Func<Task> action, CancellationToken token)
    {
        // Critical: yield so sibling loops are scheduled before any collector can hang the thread.
        await Task.Yield();
        _logger.LogInformation("Loop {Name} started (interval={Seconds}s)", name, interval.TotalSeconds);

        using var timer = new PeriodicTimer(interval);
        while (!token.IsCancellationRequested)
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Loop {Name} failed", name);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(token))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
