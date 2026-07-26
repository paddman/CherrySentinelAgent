namespace CherrySentinel.Core.Configuration;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string Name { get; set; } = "Cherry Sentinel Agent";
    public string AgentId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = Environment.MachineName;
    public string DataDirectory { get; set; } = @"C:\ProgramData\CherrySentinel\Agent";
    public string ConfigPath { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public int HeartbeatSeconds { get; set; } = 60;
    public int CollectionIntervalSeconds { get; set; } = 5;
    /// <summary>When true (default), no destructive response actions execute without explicit central approval.</summary>
    public bool DetectOnly { get; set; } = true;
}

public sealed class CentralServerOptions
{
    public const string SectionName = "Server";

    public string Url { get; set; } = "https://sentinel.example.local";
    // Back-compat
    public string BaseUrl
    {
        get => Url;
        set => Url = value;
    }

    public bool EnableMtls { get; set; }
    public string? ClientCertificatePath { get; set; }
    public string? ClientCertificatePassword { get; set; }
    public string? CaCertificatePath { get; set; }
    public bool AllowUntrustedServerCertificate { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public int HeartbeatIntervalSeconds { get; set; } = 60;
    public int FlushIntervalSeconds { get; set; } = 15;
    public int BatchSize { get; set; } = 200;
    public int OfflineQueueLimit { get; set; } = 100_000;
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string DatabaseFileName { get; set; } = "agent.db";
    public int RetentionDays { get; set; } = 14;
    public long MaxDatabaseSizeMb { get; set; } = 1024;
    public int MaintenanceIntervalMinutes { get; set; } = 60;
    public int OfflineQueueLimit { get; set; } = 100_000;
    public int MaxLogFileBytes { get; set; } = 50 * 1024 * 1024;
}

public sealed class EventLogCollectorOptions
{
    public const string SectionName = "Collectors";

    public bool SecurityEvents { get; set; } = true;
    public bool Enabled
    {
        get => SecurityEvents;
        set => SecurityEvents = value;
    }

    public List<EventChannelWatch> Channels { get; set; } =
    [
        new()
        {
            LogName = "Security",
            EventIds = [4624, 4625, 4648, 4672, 4688, 4697, 4698, 4720, 4728, 4732, 5156, 5157]
        },
        new()
        {
            LogName = "System",
            EventIds = [7045]
        }
    ];
}

public sealed class EventChannelWatch
{
    public string LogName { get; set; } = "Security";
    public List<int> EventIds { get; set; } = [];
}

public sealed class NetworkCollectorOptions
{
    public const string SectionName = "Collectors";

    public bool NetworkConnections { get; set; } = true;
    public bool Enabled
    {
        get => NetworkConnections;
        set => NetworkConnections = value;
    }

    public int PollIntervalSeconds { get; set; } = 5;
    public bool CaptureUdp { get; set; } = true;
    public bool ResolveProcessDetails { get; set; } = true;
    public bool HashNewExecutables { get; set; } = true;
    public bool ResolveServices { get; set; } = true;
}

public sealed class ProcessCollectorOptions
{
    public const string SectionName = "Collectors";

    public bool Processes { get; set; } = true;
    public bool Services { get; set; } = true;
    public bool Enabled
    {
        get => Processes;
        set => Processes = value;
    }

    public int PollIntervalSeconds { get; set; } = 30;
    public bool HashExecutables { get; set; } = true;
    public bool UseWmiFallback { get; set; } = true;
}

public sealed class ScheduledTaskCollectorOptions
{
    public const string SectionName = "Collectors";

    public bool ScheduledTasks { get; set; } = true;
    public bool Enabled
    {
        get => ScheduledTasks;
        set => ScheduledTasks = value;
    }

    public int PollIntervalSeconds { get; set; } = 60;
}

public sealed class DetectionOptions
{
    public const string SectionName = "Detection";

    public bool Enabled { get; set; } = true;
    public string RulesPath { get; set; } = "config/rules.json";
    public string AllowlistPath { get; set; } = "config/allowlist.json";
    public int EvaluationIntervalSeconds { get; set; } = 10;
    public List<string> AllowlistUsernames { get; set; } = [];
    public List<string> AllowlistSourceIps { get; set; } = [];
}

public sealed class ResponseOptions
{
    public const string SectionName = "Response";

    /// <summary>Default true: detect-only, no block/kill without central approval.</summary>
    public bool DetectOnly { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool AllowNetworkIsolation { get; set; }
    public bool AllowProcessTerminate { get; set; }
    public bool LogOnlyMode { get; set; } = true;
    public string EvidenceDirectory { get; set; } = @"C:\ProgramData\CherrySentinel\Agent\evidence";
}

public sealed class LoggingPathsOptions
{
    public const string SectionName = "LoggingPaths";

    public string Directory { get; set; } = @"C:\ProgramData\CherrySentinel\Agent\logs";
    public string FileName { get; set; } = "agent-.log";
    public int RetainedFileCountLimit { get; set; } = 31;
    public long FileSizeLimitBytes { get; set; } = 50 * 1024 * 1024;
}
