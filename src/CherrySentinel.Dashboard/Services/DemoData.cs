using CherrySentinel.Shared.Enums;
using CherrySentinel.Shared.Models;

namespace CherrySentinel.Dashboard.Services;

/// <summary>
/// Sample SOC data so the desktop app is useful offline / before agents report.
/// </summary>
public static class DemoData
{
    public static List<Incident> Incidents()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new Incident
            {
                IncidentId = "demo-inc-001",
                Title = "Internal Password Spray",
                RuleId = "INTERNAL_PASSWORD_SPRAY",
                Severity = Severity.High,
                SourceIp = "10.0.105.35",
                SourceHost = "SRC-35",
                DestinationIp = "10.0.105.190",
                DestinationHost = "DEST-190",
                DestinationPort = 80,
                ProcessId = 1684,
                ProcessName = "svchost.exe",
                ProcessPath = @"C:\Windows\System32\svchost.exe",
                Services = ["IPTVManagementService"],
                ServiceAccount = "LocalSystem",
                LogonProcess = "Advapi",
                LogonType = 8,
                FailedAttempts = 880,
                DistinctUsernames = 11,
                SuccessfulLoginDetected = false,
                FirstSeen = now.AddMinutes(-25),
                LastSeen = now.AddMinutes(-18),
                Status = "Open",
                EvidenceEvents =
                [
                    new EvidenceEventSummary { EventId = 4625, TimestampUtc = now.AddMinutes(-24), Username = "admin", SourceIp = "10.0.105.35", Status = "0xC000006D" },
                    new EvidenceEventSummary { EventId = 4625, TimestampUtc = now.AddMinutes(-23), Username = "root", SourceIp = "10.0.105.35", Status = "0xC000006D" },
                    new EvidenceEventSummary { EventId = 4625, TimestampUtc = now.AddMinutes(-20), Username = "guest", SourceIp = "10.0.105.35", Status = "0xC000006D" }
                ]
            },
            new Incident
            {
                IncidentId = "demo-inc-002",
                Title = "Network Logon Burst",
                RuleId = "NETWORK_LOGON_BURST",
                Severity = Severity.Medium,
                SourceIp = "10.0.105.190",
                SourceHost = "DEST-190",
                DestinationIp = "10.0.105.200",
                DestinationHost = "FILE-200",
                DestinationPort = 445,
                Username = "admin",
                LogonType = 3,
                SuccessfulLoginDetected = true,
                FailedAttempts = 0,
                FirstSeen = now.AddMinutes(-12),
                LastSeen = now.AddMinutes(-10),
                Status = "Open"
            },
            new Incident
            {
                IncidentId = "demo-inc-003",
                Title = "New Service Installed",
                RuleId = "NEW_SERVICE_INSTALLED",
                Severity = Severity.High,
                DestinationHost = "FILE-200",
                DestinationIp = "10.0.105.200",
                ProcessName = "services.exe",
                Services = ["UpdateHelper"],
                FirstSeen = now.AddMinutes(-8),
                LastSeen = now.AddMinutes(-8),
                Status = "Open"
            }
        ];
    }

    public static List<ThreatCampaign> Campaigns()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            new ThreatCampaign
            {
                CampaignId = "demo-camp-001",
                Title = "Lateral movement chain (2 hops)",
                Severity = Severity.Critical,
                FirstSeenUtc = now.AddMinutes(-25),
                LastSeenUtc = now.AddMinutes(-10),
                Status = "Open",
                ThreatCategories = ["credential_access", "lateral_movement", "persistence"],
                InvolvedHosts = ["SRC-35", "DEST-190", "FILE-200"],
                InvolvedIps = ["10.0.105.35", "10.0.105.190", "10.0.105.200"],
                InvolvedUsernames = ["admin", "root", "guest"],
                RelatedIncidentIds = ["demo-inc-001", "demo-inc-002", "demo-inc-003"],
                Summary = "Password spray from 10.0.105.35, pivot via 190, persistence on 200.",
                Hops =
                [
                    new ThreatHop
                    {
                        TimestampUtc = now.AddMinutes(-20),
                        FromIp = "10.0.105.35",
                        FromHost = "SRC-35",
                        ToIp = "10.0.105.190",
                        ToHost = "DEST-190",
                        Port = 80,
                        Technique = "password_spray",
                        ProcessId = 1684,
                        ProcessName = "svchost.exe",
                        ServiceNames = "IPTVManagementService",
                        IncidentId = "demo-inc-001",
                        Evidence = "880 failed logons, 11 usernames"
                    },
                    new ThreatHop
                    {
                        TimestampUtc = now.AddMinutes(-11),
                        FromIp = "10.0.105.190",
                        FromHost = "DEST-190",
                        ToIp = "10.0.105.200",
                        ToHost = "FILE-200",
                        Port = 445,
                        Technique = "network_logon",
                        Username = "admin",
                        LogonType = 3,
                        IncidentId = "demo-inc-002",
                        Evidence = "successful type-3 logon burst"
                    }
                ]
            }
        ];
    }

    public static List<AgentRow> Agents() =>
    [
        new("agent-src", "SRC-35", "10.0.105.35", "1.0.0", "Healthy", DateTimeOffset.UtcNow.AddSeconds(-40)),
        new("agent-dest", "DEST-190", "10.0.105.190", "1.0.0", "Healthy", DateTimeOffset.UtcNow.AddSeconds(-55)),
        new("agent-file", "FILE-200", "10.0.105.200", "1.0.0", "Healthy", DateTimeOffset.UtcNow.AddMinutes(-2))
    ];
}

public sealed record AgentRow(
    string AgentId,
    string ComputerName,
    string? HostIp,
    string? Version,
    string? Status,
    DateTimeOffset LastSeenUtc);
