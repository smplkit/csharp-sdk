using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Smplkit.Logging.Adapters;

/// <summary>
/// Logging adapter for Microsoft.Extensions.Logging.
/// </summary>
/// <remarks>
/// <para>Sits in the host's logging pipeline as an <see cref="ILoggerProvider"/>
/// (so every <c>ILoggerFactory.CreateLogger(name)</c> call is observable for
/// auto-discovery) and as <see cref="IConfigureOptions{LoggerFilterOptions}"/> +
/// <see cref="IOptionsChangeTokenSource{LoggerFilterOptions}"/> (so server-managed
/// log levels propagate through the host's filter pipeline and gate output to
/// every other registered provider — Console, Debug, Serilog-via-MEL, etc.).</para>
/// <para>Wire it via <c>AddSmplkit</c>:</para>
/// <code>
/// services.AddLogging(b => b.AddSmplkit(client));
/// </code>
/// <para>Direct construction is supported for tests; production code should use
/// the extension so all four DI registrations (provider, configure-options,
/// change-token-source, smplkit adapter) happen in one call.</para>
/// </remarks>
public sealed class MicrosoftLoggingAdapter
    : ILoggingAdapter, ILoggerProvider, IConfigureOptions<LoggerFilterOptions>, IOptionsChangeTokenSource<LoggerFilterOptions>
{
    private readonly HashSet<string> _knownCategories = new();
    private readonly Dictionary<string, MsLogLevel> _appliedLevels = new();
    private readonly object _lock = new();
    private CancellationTokenSource _trigger = new();
    private Action<string, LogLevel>? _hook;
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "microsoft-logging";

    // The change-token source applies to the default (unnamed) LoggerFilterOptions instance.
    string IOptionsChangeTokenSource<LoggerFilterOptions>.Name => Options.DefaultName;

    /// <summary>
    /// Captures <paramref name="categoryName"/> for auto-discovery and returns
    /// a no-op logger. Filtering is applied at the host level via the
    /// <see cref="LoggerFilterOptions"/> rules emitted by <see cref="Configure"/>.
    /// </summary>
    public ILogger CreateLogger(string categoryName)
    {
        bool isNew;
        lock (_lock)
        {
            isNew = _knownCategories.Add(categoryName);
        }
        if (isNew)
            _hook?.Invoke(categoryName, LogLevel.Trace);
        // Return our own no-op logger (NOT NullLogger.Instance — that's a
        // sentinel some host configurations treat as "skip this provider",
        // which would defeat our auto-discovery role). This logger reports
        // IsEnabled=true so MEL's host-level filter rules govern; Log itself
        // is a no-op because we are a level-controller, not a sink.
        return PassThroughLogger.Instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<DiscoveredLogger> Discover()
    {
        lock (_lock)
        {
            return _knownCategories
                .Select(c => new DiscoveredLogger(
                    c,
                    _appliedLevels.TryGetValue(c, out var l) ? ToSmplLevel(l) : LogLevel.Trace))
                .ToList();
        }
    }

    /// <inheritdoc />
    public void ApplyLevel(string loggerName, LogLevel level)
    {
        var msLevel = ToMsLevel(level);
        lock (_lock)
        {
            _appliedLevels[loggerName] = msLevel;
            _knownCategories.Add(loggerName);
        }
        TriggerOptionsReload();
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

    /// <summary>
    /// Adds a per-category filter rule for every logger whose level has been
    /// applied. Categories observed via <see cref="CreateLogger"/> but not yet
    /// assigned a managed level are NOT given a rule, so the host's existing
    /// filter configuration applies to them unchanged.
    /// </summary>
    public void Configure(LoggerFilterOptions options)
    {
        lock (_lock)
        {
            foreach (var (category, msLevel) in _appliedLevels)
            {
                options.Rules.Add(new LoggerFilterRule(
                    providerName: null,
                    categoryName: category,
                    logLevel: msLevel,
                    filter: null));
            }
        }
    }

    /// <inheritdoc />
    public IChangeToken GetChangeToken()
    {
        lock (_lock)
        {
            return new CancellationChangeToken(_trigger.Token);
        }
    }

    private void TriggerOptionsReload()
    {
        CancellationTokenSource old;
        lock (_lock)
        {
            old = _trigger;
            _trigger = new CancellationTokenSource();
        }
        old.Cancel();
        old.Dispose();
    }

    /// <summary>Releases resources used by this adapter.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UninstallHook();
        lock (_lock)
        {
            _trigger.Dispose();
        }
    }

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

    private sealed class PassThroughLogger : ILogger
    {
        public static readonly PassThroughLogger Instance = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(MsLogLevel logLevel) => true;
        public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // No-op — the smplkit adapter is a level controller, not an output sink.
        }
    }
}
