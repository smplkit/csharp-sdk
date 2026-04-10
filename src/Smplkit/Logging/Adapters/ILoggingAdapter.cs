namespace Smplkit.Logging.Adapters;

/// <summary>
/// Contract for integrating a logging framework with the smplkit Logging service
/// (e.g., Microsoft.Extensions.Logging, Serilog).
/// </summary>
public interface ILoggingAdapter
{
    /// <summary>Gets the adapter name (e.g., "microsoft-logging").</summary>
    string Name { get; }

    /// <summary>
    /// Returns all loggers currently known to the framework.
    /// </summary>
    IReadOnlyList<DiscoveredLogger> Discover();

    /// <summary>
    /// Sets the level on a specific logger.
    /// </summary>
    /// <param name="loggerName">The logger name.</param>
    /// <param name="level">smplkit log level.</param>
    void ApplyLevel(string loggerName, LogLevel level);

    /// <summary>
    /// Installs a callback that is invoked whenever a new logger is created.
    /// </summary>
    void InstallHook(Action<string, LogLevel> onNewLogger);

    /// <summary>Removes the callback installed by <see cref="InstallHook"/>.</summary>
    void UninstallHook();
}
