using CherrySentinel.Shared.Enums;

namespace CherrySentinel.Detection.Rules;

public sealed class DetectionRuleSet
{
    public List<DetectionRuleDefinition> Rules { get; set; } = [];
}

public sealed class DetectionRuleDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Severity { get; set; } = "Medium";
    public string Type { get; set; } = "threshold";
    public string Description { get; set; } = string.Empty;

    public List<int> EventIds { get; set; } = [];
    public int WindowMinutes { get; set; } = 5;
    public int MinEventCount { get; set; }
    public int MinDistinctUsernames { get; set; }
    public int MaxDistinctUsernames { get; set; }
    public int MinDistinctSources { get; set; }
    public int MinDistinctDestinations { get; set; }
    public bool RequireSameSource { get; set; }
    public bool RequireSameDestination { get; set; }
    public List<string> GroupBy { get; set; } = [];
    public int CooldownMinutes { get; set; } = 10;

    // Sequence rules
    public int FailureEventId { get; set; } = 4625;
    public int SuccessEventId { get; set; } = 4624;
    public int PrivilegeEventId { get; set; }
    public int MinFailures { get; set; }
    public List<string> MatchFields { get; set; } = [];

    // Suspicious names
    public List<string> SuspiciousUsernames { get; set; } = [];

    // Network rules
    public List<int> AuthPorts { get; set; } = [];

    public Severity ParsedSeverity =>
        Enum.TryParse<Severity>(Severity, true, out var s) ? s : Shared.Enums.Severity.Medium;
}
