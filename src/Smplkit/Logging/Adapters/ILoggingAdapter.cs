namespace Smplkit.Logging.Adapters;

/// <summary>
/// Contract for pluggable logging framework integration.
///
/// Adapters bridge the smplkit logging runtime to a specific logging framework
/// (e.g., Microsoft.Extensions.Logging, Serilog).
/// </summary>
public interface ILoggingAdapter
{
    /// <summary>Human-readable adapter name for diagnostics (e.g., "microsoft-logging").</summary>
    string Name { get; }

    /// <summary>
    /// Scan the runtime for existing loggers.
    /// Returns a list of discovered loggers with their names and levels.
    /// </summary>
    IReadOnlyList<DiscoveredLogger> Discover();

    /// <summary>
    /// Set the level on a specific logger.
    /// </summary>
    /// <param name="loggerName">The original (non-normalized) logger name.</param>
    /// <param name="level">smplkit log level.</param>
    void ApplyLevel(string loggerName, LogLevel level);

    /// <summary>
    /// Install continuous discovery hook.
    /// The callback receives (original_name, level) whenever a new logger is created.
    /// </summary>
    void InstallHook(Action<string, LogLevel> onNewLogger);

    /// <summary>Remove the hook installed by InstallHook. Called on Close().</summary>
    void UninstallHook();
}
