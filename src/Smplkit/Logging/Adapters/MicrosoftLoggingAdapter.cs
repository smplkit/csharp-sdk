using Microsoft.Extensions.Logging;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Smplkit.Logging.Adapters;

/// <summary>
/// Logging adapter for Microsoft.Extensions.Logging.
///
/// Wraps an <see cref="ILoggerFactory"/> to track all loggers created through it,
/// enabling discovery and dynamic level control from the smplkit runtime.
///
/// Usage:
/// <code>
/// var adapter = new MicrosoftLoggingAdapter(innerFactory);
/// // Use adapter.Factory instead of innerFactory to create loggers
/// </code>
/// </summary>
public sealed class MicrosoftLoggingAdapter : ILoggingAdapter, IDisposable
{
    private readonly ILoggerFactory _innerFactory;
    private readonly Dictionary<string, TrackedLogger> _loggers = new();
    private readonly object _lock = new();
    private Action<string, LogLevel>? _hook;

    /// <summary>
    /// Initializes a new instance wrapping the given logger factory.
    /// </summary>
    /// <param name="innerFactory">The real <see cref="ILoggerFactory"/> to delegate to.</param>
    public MicrosoftLoggingAdapter(ILoggerFactory innerFactory)
    {
        _innerFactory = innerFactory ?? throw new ArgumentNullException(nameof(innerFactory));
        Factory = new TrackingLoggerFactory(this);
    }

    /// <summary>
    /// Parameterless constructor for auto-loading. The adapter will not track loggers
    /// until <see cref="Factory"/> is used by the application.
    /// </summary>
    public MicrosoftLoggingAdapter()
        : this(new LoggerFactory())
    {
    }

    /// <inheritdoc />
    public string Name => "microsoft-logging";

    /// <summary>
    /// The wrapping <see cref="ILoggerFactory"/> that tracks logger creation.
    /// Use this instead of the inner factory in your application.
    /// </summary>
    public ILoggerFactory Factory { get; }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredLogger> Discover()
    {
        lock (_lock)
        {
            return _loggers.Values
                .Select(t => new DiscoveredLogger(t.CategoryName, ToSmplLevel(t.MinLevel)))
                .ToList();
        }
    }

    /// <inheritdoc />
    public void ApplyLevel(string loggerName, LogLevel level)
    {
        var msLevel = ToMsLevel(level);
        lock (_lock)
        {
            if (_loggers.TryGetValue(loggerName, out var tracked))
            {
                tracked.MinLevel = msLevel;
            }
            else
            {
                // Create a tracking entry even if the logger hasn't been created yet
                _loggers[loggerName] = new TrackedLogger(loggerName, _innerFactory.CreateLogger(loggerName), msLevel);
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

    /// <summary>Disposes the inner factory if it is owned by this adapter.</summary>
    public void Dispose()
    {
        UninstallHook();
        if (_innerFactory is IDisposable disposable)
            disposable.Dispose();
    }

    internal ILogger GetOrCreateLogger(string categoryName)
    {
        TrackedLogger tracked;
        bool isNew = false;

        lock (_lock)
        {
            if (!_loggers.TryGetValue(categoryName, out tracked!))
            {
                var inner = _innerFactory.CreateLogger(categoryName);
                tracked = new TrackedLogger(categoryName, inner, MsLogLevel.Trace);
                _loggers[categoryName] = tracked;
                isNew = true;
            }
        }

        if (isNew)
        {
            _hook?.Invoke(categoryName, ToSmplLevel(tracked.MinLevel));
        }

        return new LevelGatingLogger(tracked);
    }

    // ------------------------------------------------------------------
    // Level mapping
    // ------------------------------------------------------------------

    internal static MsLogLevel ToMsLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => MsLogLevel.Trace,
        LogLevel.Debug => MsLogLevel.Debug,
        LogLevel.Info => MsLogLevel.Information,
        LogLevel.Warn => MsLogLevel.Warning,
        LogLevel.Error => MsLogLevel.Error,
        LogLevel.Fatal => MsLogLevel.Critical,
        LogLevel.Silent => MsLogLevel.None,
        _ => MsLogLevel.Information,
    };

    internal static LogLevel ToSmplLevel(MsLogLevel level) => level switch
    {
        MsLogLevel.Trace => LogLevel.Trace,
        MsLogLevel.Debug => LogLevel.Debug,
        MsLogLevel.Information => LogLevel.Info,
        MsLogLevel.Warning => LogLevel.Warn,
        MsLogLevel.Error => LogLevel.Error,
        MsLogLevel.Critical => LogLevel.Fatal,
        MsLogLevel.None => LogLevel.Silent,
        _ => LogLevel.Info,
    };

    // ------------------------------------------------------------------
    // Inner types
    // ------------------------------------------------------------------

    internal sealed class TrackedLogger
    {
        public string CategoryName { get; }
        public ILogger Inner { get; }
        public MsLogLevel MinLevel { get; set; }

        public TrackedLogger(string categoryName, ILogger inner, MsLogLevel minLevel)
        {
            CategoryName = categoryName;
            Inner = inner;
            MinLevel = minLevel;
        }
    }

    /// <summary>
    /// An <see cref="ILogger"/> wrapper that gates log calls based on the tracked minimum level.
    /// </summary>
    private sealed class LevelGatingLogger : ILogger
    {
        private readonly TrackedLogger _tracked;

        public LevelGatingLogger(TrackedLogger tracked)
        {
            _tracked = tracked;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => _tracked.Inner.BeginScope(state);

        public bool IsEnabled(MsLogLevel logLevel)
            => logLevel >= _tracked.MinLevel && _tracked.Inner.IsEnabled(logLevel);

        public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _tracked.Inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>
    /// An <see cref="ILoggerFactory"/> that delegates to the real factory but tracks every
    /// <see cref="CreateLogger"/> call in the adapter's dictionary.
    /// </summary>
    private sealed class TrackingLoggerFactory : ILoggerFactory
    {
        private readonly MicrosoftLoggingAdapter _adapter;

        public TrackingLoggerFactory(MicrosoftLoggingAdapter adapter)
        {
            _adapter = adapter;
        }

        public ILogger CreateLogger(string categoryName)
            => _adapter.GetOrCreateLogger(categoryName);

        public void AddProvider(ILoggerProvider provider)
            => _adapter._innerFactory.AddProvider(provider);

        public void Dispose()
        {
            // The adapter owns disposal of the inner factory
        }
    }
}
