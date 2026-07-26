using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using CherrySentinel.Core.Abstractions;
using CherrySentinel.Core.Compatibility;
using CherrySentinel.Core.Configuration;
using CherrySentinel.Core.Security;
using CherrySentinel.Response.Firewall;
using CherrySentinel.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CherrySentinel.Response;

/// <summary>
/// Response engine. Default = detect-only (no block/kill without explicit Central approval).
/// Only allowlisted action types are accepted. No arbitrary shell from Central.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LocalResponseExecutor : IResponseExecutor
{
    private readonly ResponseOptions _options;
    private readonly AgentOptions _agentOptions;
    private readonly FirewallBlocker _firewall;
    private readonly IEvidenceCollector? _evidence;
    private readonly ILogger<LocalResponseExecutor> _logger;

    public LocalResponseExecutor(
        IOptions<ResponseOptions> options,
        IOptions<AgentOptions> agentOptions,
        FirewallBlocker firewall,
        ILogger<LocalResponseExecutor> logger,
        IEvidenceCollector? evidence = null)
    {
        _options = options.Value;
        _agentOptions = agentOptions.Value;
        _firewall = firewall;
        _logger = logger;
        _evidence = evidence;
    }

    public Task<ResponseActionRecord> ExecuteAsync(DetectionAlert alert, CancellationToken cancellationToken)
    {
        var record = BaseRecord("LogOnly", alert.AlertId, string.Empty);
        record.Requester = "local-detection";
        record.Reason = $"Detection {alert.RuleId}: {alert.Title}";
        record.Status = "Completed";
        record.BeforeState = "n/a";
        record.Result = "detect-only: alert logged, no containment";
        record.RollbackCommand = string.Empty;
        record.AuditLog = Audit(record, "detect-only default; no destructive action");
        _logger.LogWarning("DETECT-ONLY response for {Rule} AlertId={AlertId}", alert.RuleId, alert.AlertId);
        return Task.FromResult(record);
    }

    public async Task<ResponseActionRecord> ExecuteRequestAsync(ResponseActionRequest request, CancellationToken cancellationToken)
    {
        var record = BaseRecord(request.ActionType, request.AlertId ?? string.Empty, request.IncidentId ?? string.Empty);
        record.RequestId = string.IsNullOrWhiteSpace(request.RequestId) ? record.RequestId : request.RequestId;
        record.Requester = request.Requester;
        record.Reason = request.Reason;
        record.Approved = request.Approved;
        record.ApprovalId = request.ApprovalId;
        record.RequiresApproval = RequiresApproval(request.ActionType);

        if (!EventDataSanitizer.IsAllowlistedCommand(request.ActionType))
        {
            record.Status = "Rejected";
            record.Error = "Action type not in allowlist (arbitrary commands forbidden).";
            record.AuditLog = Audit(record, "rejected allowlist");
            return record;
        }

        // Hard rule: destructive actions need explicit Central approval.
        if (record.RequiresApproval && !request.Approved)
        {
            record.Status = "PendingApproval";
            record.Result = "Blocked by policy: explicit Central Server approval required.";
            record.AuditLog = Audit(record, "missing approval");
            return record;
        }

        if ((_options.DetectOnly || _agentOptions.DetectOnly || _options.LogOnlyMode) &&
            record.RequiresApproval &&
            !request.Approved)
        {
            record.Status = "Skipped";
            record.Result = "DetectOnly=true";
            record.AuditLog = Audit(record, "detect-only");
            return record;
        }

        try
        {
            switch (request.ActionType)
            {
                case "LogOnly":
                    record.Status = "Completed";
                    record.Result = "logged";
                    break;

                case "BlockDestinationIp":
                case "BlockRemoteIp":
                    {
                        var ip = request.TargetIp ?? throw new ArgumentException("TargetIp required");
                        var rule = $"CSA-Block-{SanitizeToken(ip)}-{record.RequestId[..8]}";
                        var dir = request.ActionType == "BlockDestinationIp" ? "out" : "in";
                        var result = _firewall.BlockIp(ip, dir, rule, request.Reason);
                        ApplyFirewall(record, result);
                        break;
                    }

                case "RemoveFirewallBlock":
                    {
                        var rule = $"CSA-Block-{SanitizeToken(request.TargetIp ?? "x")}-*";
                        // Require exact rule name in TargetIp field prefix CSA-
                        var ruleName = request.ServiceName ?? request.TargetIp;
                        if (string.IsNullOrWhiteSpace(ruleName) || !ruleName.StartsWith("CSA-", StringComparison.OrdinalIgnoreCase))
                        {
                            // ServiceName carries rule name when removing
                            throw new ArgumentException("Provide CSA- rule name in ServiceName for RemoveFirewallBlock.");
                        }

                        var result = _firewall.RemoveRule(ruleName);
                        ApplyFirewall(record, result);
                        break;
                    }

                case "StopService":
                    await ControlServiceAsync(record, request.ServiceName, disable: false, cancellationToken);
                    break;

                case "DisableService":
                    await ControlServiceAsync(record, request.ServiceName, disable: true, cancellationToken);
                    break;

                case "StopScheduledTask":
                    ControlTask(record, request, disable: false);
                    break;

                case "DisableScheduledTask":
                    ControlTask(record, request, disable: true);
                    break;

                case "TerminateProcess":
                    TerminateProcess(record, request.ProcessId);
                    break;

                case "ExportEvidence":
                    {
                        if (_evidence is null)
                        {
                            record.Status = "Failed";
                            record.Error = "Evidence collector not registered";
                            break;
                        }

                        var incident = new Incident
                        {
                            IncidentId = request.IncidentId ?? record.RequestId,
                            Title = "Evidence export",
                            SourceIp = request.TargetIp
                        };
                        var path = await _evidence.CollectAndExportZipAsync(incident, cancellationToken);
                        record.Status = "Completed";
                        record.Result = path;
                        record.BeforeState = "n/a";
                        record.RollbackCommand = $"Remove-Item -LiteralPath '{path}'";
                        break;
                    }

                case "QuarantineHost":
                    {
                        if (!WindowsCompatibility.SupportsAdvancedNetworkIsolation)
                        {
                            // Server 2012-compatible quarantine: block all inbound except management is too broad;
                            // apply conservative inbound block of non-local with netsh profile — require approval only.
                            record.Status = "Completed";
                            record.BeforeState = _firewall.CaptureFirewallStatus();
                            var r1 = _firewall.BlockIp("any", "in", $"CSA-Quarantine-In-{record.RequestId[..8]}", request.Reason);
                            // netsh may not accept "any" on older builds — fall back message
                            record.Result = r1.Success
                                ? "Quarantine inbound rule applied (Server 2012-compatible netsh)"
                                : $"Quarantine limited: {r1.Error}. Manual isolation recommended.";
                            record.RollbackCommand = r1.RollbackCommand;
                            record.Status = r1.Success ? "Completed" : "Failed";
                            record.Error = r1.Error;
                        }
                        else
                        {
                            record.Status = "Completed";
                            record.Result = "Quarantine signaled (operator must complete network isolation via NAC/firewall).";
                            record.BeforeState = _firewall.CaptureFirewallStatus();
                            record.RollbackCommand = "# remove quarantine rules with RemoveFirewallBlock";
                        }

                        break;
                    }

                default:
                    record.Status = "Rejected";
                    record.Error = "Unhandled action type";
                    break;
            }
        }
        catch (Exception ex)
        {
            record.Status = "Failed";
            record.Error = ex.Message;
            _logger.LogError(ex, "Response action {Type} failed", request.ActionType);
        }

        record.AuditLog = Audit(record, "executed");
        return record;
    }

    private static bool RequiresApproval(string actionType) =>
        actionType is not ("LogOnly" or "ExportEvidence");

    private void ApplyFirewall(ResponseActionRecord record, FirewallChangeResult result)
    {
        record.BeforeState = result.BeforeState;
        record.Result = result.Result;
        record.RollbackCommand = result.RollbackCommand;
        record.Status = result.Success ? "Completed" : "Failed";
        record.Error = result.Error;
    }

    private Task ControlServiceAsync(ResponseActionRecord record, string? serviceName, bool disable, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.IndexOfAny(['"', ';', '&', '|']) >= 0)
        {
            throw new ArgumentException("Invalid service name");
        }

        using var searcher = new ManagementObjectSearcher(
            $"SELECT Name, State, StartMode FROM Win32_Service WHERE Name = '{EscapeWmi(serviceName)}'");
        ManagementObject? svc = null;
        foreach (ManagementObject obj in searcher.Get())
        {
            svc = obj;
            break;
        }

        if (svc is null)
        {
            record.Status = "Failed";
            record.Error = "Service not found";
            return Task.CompletedTask;
        }

        var state = svc["State"]?.ToString() ?? "Unknown";
        var mode = svc["StartMode"]?.ToString() ?? "Unknown";
        record.BeforeState = $"State={state};StartMode={mode}";

        if (disable)
        {
            svc.InvokeMethod("ChangeStartMode", new object[] { "Disabled" });
            record.RollbackCommand = $"sc.exe config \"{serviceName}\" start= demand";
        }

        svc.InvokeMethod("StopService", Array.Empty<object>());
        record.Result = disable ? "service stopped and disabled" : "service stopped";
        record.Status = "Completed";
        if (!disable)
        {
            record.RollbackCommand = $"sc.exe start \"{serviceName}\"";
        }

        return Task.CompletedTask;
    }

    private void ControlTask(ResponseActionRecord record, ResponseActionRequest request, bool disable)
    {
        var path = request.TaskPath ?? "\\";
        var name = request.TaskName ?? throw new ArgumentException("TaskName required");
        if (name.Contains("..") || path.Contains(".."))
        {
            throw new InvalidOperationException("Path traversal rejected");
        }

        var taskServiceType = Type.GetTypeFromProgID("Schedule.Service")
                              ?? throw new InvalidOperationException("Schedule.Service unavailable");
        dynamic service = Activator.CreateInstance(taskServiceType)!;
        service.Connect();
        dynamic folder = service.GetFolder(path);
        dynamic task = folder.GetTask(name);
        record.BeforeState = $"Enabled={task.Enabled}";
        if (disable)
        {
            task.Enabled = false;
            record.Result = "task disabled";
            record.RollbackCommand = $"# re-enable task {path}{name}";
        }
        else
        {
            task.Stop(0);
            record.Result = "task stopped";
            record.RollbackCommand = $"# task stop is transient for {path}{name}";
        }

        record.Status = "Completed";
    }

    private void TerminateProcess(ResponseActionRecord record, int? pid)
    {
        if (pid is null or <= 0)
        {
            throw new ArgumentException("ProcessId required");
        }

        using var proc = Process.GetProcessById(pid.Value);
        record.BeforeState = $"PID={pid};Name={proc.ProcessName}";
        proc.Kill();
        record.Result = "process terminated";
        record.RollbackCommand = "# process termination is not reversible";
        record.Status = "Completed";
    }

    private ResponseActionRecord BaseRecord(string actionType, string alertId, string incidentId) => new()
    {
        TimestampUtc = DateTimeOffset.UtcNow,
        ComputerName = _agentOptions.ComputerName,
        AgentId = _agentOptions.AgentId,
        AlertId = alertId,
        IncidentId = incidentId,
        ActionType = actionType,
        Status = "Pending"
    };

    private static string Audit(ResponseActionRecord r, string note) =>
        JsonSerializer.Serialize(new
        {
            r.RequestId,
            r.Requester,
            r.TimestampUtc,
            r.ActionType,
            r.Reason,
            r.BeforeState,
            r.Result,
            r.RollbackCommand,
            r.Status,
            r.Approved,
            r.ApprovalId,
            note
        });

    private static string EscapeWmi(string value) => value.Replace("'", "\\'");

    private static string SanitizeToken(string ip) =>
        new string(ip.Where(c => char.IsLetterOrDigit(c) || c is '.' or ':' or '-').ToArray());
}
