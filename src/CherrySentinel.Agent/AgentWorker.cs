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
    private readonly ILogger<AgentWorker> _logger;
    private readonly AgentOptions _agentOptions;
    private readonly NetworkCollectorOptions _networkOptions;
    private readonly ProcessCollectorOptions _processOptions;
    private readonly ScheduledTaskCollectorOptions _taskOptions;
    private readonly DetectionOptions _detectionOptions;
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

    private readonly List<SecurityEventRecord> _eventBuffer = [];
    private readonly List<NetworkConnectionRecord> _connectionBuffer = [];
    private readonly object _bufferSync = new();

    public AgentWorker(
        ILogger<AgentWorker> logger,
        IOptions<AgentOptions> agentOptions,
        IOptions<NetworkCollectorOptions> networkOptions,
        IOptions<ProcessCollectorOptions> processOptions,
        IOptions<ScheduledTaskCollectorOptions> taskOptions,
        IOptions<DetectionOptions> detectionOptions,
        IOptions<CentralServerOptions> centralOptions,
        IOptions<StorageOptions> storageOptions,
        ILocalStore store,
        IEventLogCollector eventLogCollector,
        INetworkCollector networkCollector,
        IProcessCollector processCollector,
        IScheduledTaskCollector taskCollector,
        IDetectionEngine detectionEngine,
        IResponseExecutor responseExecutor,
        ITransportClient transport)
    {
        _logger = logger;
        _agentOptions = agentOptions.Value;
        _networkOptions = networkOptions.Value;
        _processOptions = processOptions.Value;
        _taskOptions = taskOptions.Value;
        _detectionOptions = detectionOptions.Value;
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
        _logger.LogInformation("Cherry Sentinel Agent {Version} started. AgentId={AgentId}", _agentOptions.Version, _agentOptions.AgentId);

        var networkTask = RunLoopAsync("network", TimeSpan.FromSeconds(Math.Max(2, _networkOptions.PollIntervalSeconds)), CollectNetworkAsync, stoppingToken);
        var processTask = RunLoopAsync("process", TimeSpan.FromSeconds(Math.Max(10, _processOptions.PollIntervalSeconds)), CollectProcessAsync, stoppingToken);
        var taskTask = RunLoopAsync("tasks", TimeSpan.FromSeconds(Math.Max(30, _taskOptions.PollIntervalSeconds)), CollectTasksAsync, stoppingToken);
        var detectTask = RunLoopAsync("detect", TimeSpan.FromSeconds(Math.Max(5, _detectionOptions.EvaluationIntervalSeconds)), EvaluateDetectionsAsync, stoppingToken);
        var flushTask = RunLoopAsync("flush", TimeSpan.FromSeconds(Math.Max(5, _centralOptions.FlushIntervalSeconds)), FlushOutboundAsync, stoppingToken);
        var heartbeatTask = RunLoopAsync("heartbeat", TimeSpan.FromSeconds(Math.Max(15, _centralOptions.HeartbeatIntervalSeconds)), HeartbeatAsync, stoppingToken);
        var maintTask = RunLoopAsync("maintenance", TimeSpan.FromMinutes(Math.Max(5, _storageOptions.MaintenanceIntervalMinutes)), () => _store.RunMaintenanceAsync(stoppingToken), stoppingToken);

        try
        {
            await Task.WhenAll(networkTask, processTask, taskTask, detectTask, flushTask, heartbeatTask, maintTask);
        }
        finally
        {
            _eventLogCollector.EventReceived -= OnEventReceived;
            await _eventLogCollector.StopAsync(CancellationToken.None);
        }
    }

    private void OnEventReceived(object? sender, SecurityEventRecord e)
    {
        lock (_bufferSync)
        {
            _eventBuffer.Add(e);
        }
    }

    private async Task CollectNetworkAsync()
    {
        if (!_networkOptions.Enabled) return;
        var diffs = await _networkCollector.CollectDiffAsync(CancellationToken.None);
        if (diffs.Count == 0) return;
        await _store.SaveNetworkConnectionsAsync(diffs, CancellationToken.None);
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
    }

    private async Task CollectTasksAsync()
    {
        if (!_taskOptions.Enabled) return;
        var changes = await _taskCollector.CollectChangesAsync(CancellationToken.None);
        await _store.SaveScheduledTasksAsync(changes, CancellationToken.None);
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

        // Include recent persisted events for sliding windows.
        var from = DateTimeOffset.UtcNow.AddMinutes(-30);
        var recent = await _store.QuerySecurityEventsAsync(from, DateTimeOffset.UtcNow, cancellationToken: CancellationToken.None);
        var merged = recent.Concat(events)
            .GroupBy(e => e.EventRecordId + ":" + e.EventId + ":" + e.TimestampUtc.ToString("O"))
            .Select(g => g.First())
            .ToList();

        var alerts = await _detectionEngine.EvaluateAsync(merged, connections, CancellationToken.None);
        foreach (var alert in alerts.Where(a => !a.Suppressed))
        {
            await _store.SaveAlertAsync(alert, CancellationToken.None);
            var action = await _responseExecutor.ExecuteAsync(alert, CancellationToken.None);
            await _store.SaveResponseActionAsync(action, CancellationToken.None);
            _logger.LogWarning("ALERT {Rule} {Severity}: {Title}", alert.RuleId, alert.Severity, alert.Title);
        }
    }

    private async Task FlushOutboundAsync()
    {
        // Offline-first: if central is down, queue remains local.
        if (!await _transport.IsReachableAsync(CancellationToken.None))
        {
            return;
        }

        var batchItems = await _store.DequeueOutboundBatchAsync(_centralOptions.BatchSize, CancellationToken.None);
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
                        var ev = JsonSerializer.Deserialize<List<SecurityEventRecord>>(item.PayloadJson) ?? [];
                        batch.SecurityEvents.AddRange(ev);
                        break;
                    case QueueItemType.NetworkConnection:
                        var nc = JsonSerializer.Deserialize<List<NetworkConnectionRecord>>(item.PayloadJson) ?? [];
                        batch.NetworkConnections.AddRange(nc);
                        break;
                    case QueueItemType.ProcessSnapshot:
                        var pr = JsonSerializer.Deserialize<List<ProcessRecord>>(item.PayloadJson) ?? [];
                        batch.Processes.AddRange(pr);
                        break;
                    case QueueItemType.ServiceMap:
                        var sv = JsonSerializer.Deserialize<List<ServiceRecord>>(item.PayloadJson) ?? [];
                        batch.Services.AddRange(sv);
                        break;
                    case QueueItemType.ScheduledTask:
                        var tk = JsonSerializer.Deserialize<List<ScheduledTaskRecord>>(item.PayloadJson) ?? [];
                        batch.ScheduledTasks.AddRange(tk);
                        break;
                    case QueueItemType.DetectionAlert:
                        var al = JsonSerializer.Deserialize<DetectionAlert>(item.PayloadJson);
                        if (al is not null) batch.Alerts.Add(al);
                        break;
                }
            }
            catch (Exception ex)
            {
                await _store.MarkOutboundFailedAsync(item.Id, ex.Message, CancellationToken.None);
            }
        }

        var response = await _transport.SendBatchAsync(batch, CancellationToken.None);
        if (response is { Accepted: true })
        {
            await _store.MarkOutboundSentAsync(ids, CancellationToken.None);
            if (response.CreatedIncidentIds.Count > 0)
            {
                _logger.LogInformation("Central created incidents: {Ids}", string.Join(',', response.CreatedIncidentIds));
            }
        }
        else
        {
            foreach (var id in ids)
            {
                await _store.MarkOutboundFailedAsync(id, "ingest rejected or unreachable", CancellationToken.None);
            }
        }
    }

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
            Status = "Healthy"
        };
        await _transport.SendHeartbeatAsync(hb, CancellationToken.None);
    }

    private async Task RunLoopAsync(string name, TimeSpan interval, Func<Task> action, CancellationToken token)
    {
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
