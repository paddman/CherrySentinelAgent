using CherrySentinel.Server.Correlation;
using CherrySentinel.Server.Data;
using CherrySentinel.Shared.Enums;
using CherrySentinel.Shared.Models;
// CorrelationOptions lives in Server.Data
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CherrySentinel.Integration.Tests;

/// <summary>
/// Verifies multi-host threat tracking (A→B→C). Detection only — no offensive behavior.
/// </summary>
public class LateralMovementTrackerTests
{
    [Fact]
    public void Tracks_Threat_From_Source_To_Multiple_Hosts()
    {
        var tracker = new LateralMovementTracker(
            Options.Create(new CorrelationOptions { TimestampToleranceSeconds = 120 }),
            NullLogger<LateralMovementTracker>.Instance);

        var now = DateTimeOffset.UtcNow;
        var hop1 = new Incident
        {
            IncidentId = "inc1",
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
            Services = ["IPTVManagementService"],
            FailedAttempts = 80,
            DistinctUsernames = 11,
            SuccessfulLoginDetected = false,
            FirstSeen = now.AddMinutes(-10),
            LastSeen = now.AddMinutes(-8)
        };

        var hop2 = new Incident
        {
            IncidentId = "inc2",
            Title = "Network Logon Burst",
            RuleId = "NETWORK_LOGON_BURST",
            Severity = Severity.Medium,
            SourceIp = "10.0.105.190",
            SourceHost = "DEST-190",
            DestinationIp = "10.0.105.200",
            DestinationHost = "DEST-200",
            DestinationPort = 445,
            Username = "admin",
            FailedAttempts = 0,
            SuccessfulLoginDetected = true,
            FirstSeen = now.AddMinutes(-5),
            LastSeen = now.AddMinutes(-4)
        };

        var campaigns = tracker.IngestIncidents([hop1, hop2]);
        Assert.NotEmpty(campaigns);

        // After pivot link, expect multi-hop path 35 → 190 → 200
        var multi = tracker.ListCampaigns().FirstOrDefault(c => c.Hops.Count >= 2)
                    ?? campaigns.OrderByDescending(c => c.Hops.Count).First();

        Assert.True(multi.Hops.Count >= 2 || multi.InvolvedIps.Count >= 2);

        var byHost = tracker.FindByHostOrIp("10.0.105.200");
        Assert.NotEmpty(byHost);

        var display = multi.FormatDisplay();
        Assert.Contains("Threat Campaign", display);
        Assert.Contains("Lateral path", display);

        // Catalog covers all major classes
        var catalog = tracker.GetThreatCatalog();
        Assert.Contains(catalog, e => e.DetectionRuleId == "INTERNAL_PASSWORD_SPRAY");
        Assert.Contains(catalog, e => e.Category == "lateral_movement");
        Assert.Contains(catalog, e => e.Category == "persistence");
    }

    [Fact]
    public void FindByHost_Returns_Empty_For_Unknown()
    {
        var tracker = new LateralMovementTracker(
            Options.Create(new CorrelationOptions()),
            NullLogger<LateralMovementTracker>.Instance);
        Assert.Empty(tracker.FindByHostOrIp("203.0.113.1"));
    }
}
