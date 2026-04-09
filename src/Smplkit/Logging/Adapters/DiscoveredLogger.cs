namespace Smplkit.Logging.Adapters;

/// <summary>
/// Represents a logger discovered by an <see cref="ILoggingAdapter"/>.
/// </summary>
/// <param name="Name">The logger name as known to the framework.</param>
/// <param name="Level">The current smplkit log level.</param>
public record DiscoveredLogger(string Name, LogLevel Level);
