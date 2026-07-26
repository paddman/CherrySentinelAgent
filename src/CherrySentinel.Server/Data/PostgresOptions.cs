namespace CherrySentinel.Server.Data;

public sealed class PostgresOptions
{
    public const string SectionName = "Postgres";

    public string ConnectionString { get; set; } =
        "Host=127.0.0.1;Port=5432;Database=cherrysentinel;Username=cherrysentinel;Password=cherrysentinel";
}

public sealed class CorrelationOptions
{
    public const string SectionName = "Correlation";

    /// <summary>Timestamp tolerance in seconds when joining source outbound with destination auth.</summary>
    public int TimestampToleranceSeconds { get; set; } = 120;
}
