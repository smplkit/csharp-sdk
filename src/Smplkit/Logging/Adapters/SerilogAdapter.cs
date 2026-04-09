using Serilog.Core;
using Serilog.Events;

namespace Smplkit.Logging.Adapters;

/// <summary>
/// Logging adapter for Serilog.
///
/// Maintains a dictionary of namespace → <see cref="LoggingLevelSwitch"/> for dynamic
/// per-namespace level control. Use <see cref="GetOrCreateSwitch"/> to obtain level
/// switches for Serilog's <c>.MinimumLevel.Override()</c> configuration.
///
/// Usage:
/// <code>
/// var adapter = new SerilogAdapter();
/// Log.Logger = new LoggerConfiguration()
///     .MinimumLevel.Override("MyNamespace", adapter.GetOrCreateSwitch("MyNamespace"))
///     .CreateLogger();
/// </code>
/// </summary>
public sealed class SerilogAdapter : ILoggingAdapter
{
    private readonly Dictionary<string, LoggingLevelSwitch> _switches = new();
    private readonly object _lock = new();
    private Action<string, LogLevel>? _hook;

    /// <inheritdoc />
    public string Name => "serilog";

    /// <summary>
    /// Gets or creates a <see cref="LoggingLevelSwitch"/> for the given namespace.
    /// Use this with Serilog's <c>.MinimumLevel.Override()</c>.
    /// </summary>
    /// <param name="namespaceName">The namespace or source context name.</param>
    /// <returns>The <see cref="LoggingLevelSwitch"/> for dynamic level control.</returns>
    public LoggingLevelSwitch GetOrCreateSwitch(string namespaceName)
    {
        lock (_lock)
        {
            if (!_switches.TryGetValue(namespaceName, out var sw))
            {
                sw = new LoggingLevelSwitch(LogEventLevel.Verbose);
                _switches[namespaceName] = sw;

                _hook?.Invoke(namespaceName, ToSmplLevel(sw.MinimumLevel));
            }
            return sw;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredLogger> Discover()
    {
        lock (_lock)
        {
            return _switches
                .Select(kv => new DiscoveredLogger(kv.Key, ToSmplLevel(kv.Value.MinimumLevel)))
                .ToList();
        }
    }

    /// <inheritdoc />
    public void ApplyLevel(string loggerName, LogLevel level)
    {
        var serilogLevel = ToSerilogLevel(level);
        lock (_lock)
        {
            if (_switches.TryGetValue(loggerName, out var sw))
            {
                sw.MinimumLevel = serilogLevel;
            }
            else
            {
                _switches[loggerName] = new LoggingLevelSwitch(serilogLevel);
            }
        }
    }

    /// <inheritdoc />
    public void InstallHook(Action<string, LogLevel> onNewLogger)
    {
        _hook = onNewLogger;
    }

    /// <inheritdoc />
    public void UninstallHook()
    {
        _hook = null;
    }

    // ------------------------------------------------------------------
    // Level mapping
    // ------------------------------------------------------------------

    internal static LogEventLevel ToSerilogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => LogEventLevel.Verbose,
        LogLevel.Debug => LogEventLevel.Debug,
        LogLevel.Info => LogEventLevel.Information,
        LogLevel.Warn => LogEventLevel.Warning,
        LogLevel.Error => LogEventLevel.Error,
        LogLevel.Fatal => LogEventLevel.Fatal,
        // Silent: set to above Fatal (1 + Fatal) to suppress all events
        LogLevel.Silent => (LogEventLevel)(1 + (int)LogEventLevel.Fatal),
        _ => LogEventLevel.Information,
    };

    internal static LogLevel ToSmplLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Info,
        LogEventLevel.Warning => LogLevel.Warn,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Fatal,
        _ => level > LogEventLevel.Fatal ? LogLevel.Silent : LogLevel.Info,
    };
}
