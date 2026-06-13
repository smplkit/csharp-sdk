namespace Smplkit.Logging;

/// <summary>
/// Describes a logger source observation to register via
/// <see cref="LoggersClient.RegisterAsync"/>.
/// </summary>
/// <remarks>
/// Use this when you want to register loggers with explicit service/environment
/// overrides — useful for sample-data seeding, cross-service migration, and
/// test fixtures. Contrast with <see cref="LoggingClient.InstallAsync"/>, which
/// registers loggers discovered in the current process under the current
/// <see cref="SmplClient"/> service and environment.
/// </remarks>
public sealed class LoggerSource
{
    /// <summary>Gets the normalized logger name (e.g., "sqlalchemy.engine", "httpx").</summary>
    public string Name { get; }

    /// <summary>Gets the effective log level after framework inheritance. Always reported to the server.</summary>
    public LogLevel ResolvedLevel { get; }

    /// <summary>Gets the service name override. Null uses the default SmplClient service.</summary>
    public string? Service { get; }

    /// <summary>Gets the environment override. Null uses the default SmplClient environment.</summary>
    public string? Environment { get; }

    /// <summary>Gets the explicitly-set log level on this logger. Null if inherited.</summary>
    public LogLevel? Level { get; }

    /// <summary>
    /// Initializes a new <see cref="LoggerSource"/>.
    /// </summary>
    /// <param name="name">The normalized logger name.</param>
    /// <param name="resolvedLevel">Effective log level after framework inheritance. Required — always sent to the server.</param>
    /// <param name="service">Optional service override.</param>
    /// <param name="environment">Optional environment override.</param>
    /// <param name="level">Explicitly-set level, or null if inherited.</param>
    public LoggerSource(
        string name,
        LogLevel resolvedLevel,
        string? service = null,
        string? environment = null,
        LogLevel? level = null)
    {
        Name = name;
        ResolvedLevel = resolvedLevel;
        Service = service;
        Environment = environment;
        Level = level;
    }

    /// <inheritdoc />
    public override string ToString() => $"LoggerSource(Name={Name}, Service={Service}, Environment={Environment})";
}
