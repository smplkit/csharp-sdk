namespace Smplkit;

/// <summary>
/// Log severity levels used by the Smpl Logging service.
/// </summary>
public enum LogLevel
{
    /// <summary>Trace-level logging.</summary>
    Trace,

    /// <summary>Debug-level logging.</summary>
    Debug,

    /// <summary>Informational logging.</summary>
    Info,

    /// <summary>Warning-level logging.</summary>
    Warn,

    /// <summary>Error-level logging.</summary>
    Error,

    /// <summary>Fatal-level logging.</summary>
    Fatal,

    /// <summary>Silent — suppresses all log output.</summary>
    Silent,
}

/// <summary>
/// Extension methods for <see cref="LogLevel"/>.
/// </summary>
public static class LogLevelExtensions
{
    /// <summary>
    /// Returns the string representation of a <see cref="LogLevel"/>.
    /// </summary>
    /// <param name="level">The log level.</param>
    /// <returns>The uppercase string (e.g., "INFO", "ERROR").</returns>
    public static string ToWireString(this LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Fatal => "FATAL",
        LogLevel.Silent => "SILENT",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>
    /// Parses a string representation to a <see cref="LogLevel"/>.
    /// </summary>
    /// <param name="wire">The uppercase string (e.g., "INFO", "ERROR").</param>
    /// <returns>The parsed <see cref="LogLevel"/>.</returns>
    public static LogLevel ParseLogLevel(string wire) => wire switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Info,
        "WARN" => LogLevel.Warn,
        "ERROR" => LogLevel.Error,
        "FATAL" => LogLevel.Fatal,
        "SILENT" => LogLevel.Silent,
        _ => throw new ArgumentException($"Unknown log level: {wire}", nameof(wire)),
    };
}
