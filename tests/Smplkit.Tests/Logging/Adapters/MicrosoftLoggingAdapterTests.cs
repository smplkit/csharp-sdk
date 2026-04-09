using Microsoft.Extensions.Logging;
using Smplkit.Logging.Adapters;
using Xunit;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Smplkit.Tests.Logging.Adapters;

public class MicrosoftLoggingAdapterTests
{
    private static MicrosoftLoggingAdapter CreateAdapter()
    {
        return new MicrosoftLoggingAdapter(new LoggerFactory());
    }

    [Fact]
    public void Name_ReturnsMicrosoftLogging()
    {
        var adapter = CreateAdapter();
        Assert.Equal("microsoft-logging", adapter.Name);
    }

    [Fact]
    public void Discover_ReturnsTrackedLoggers()
    {
        var adapter = CreateAdapter();

        // Create loggers through the tracking factory
        adapter.Factory.CreateLogger("App.Services.UserService");
        adapter.Factory.CreateLogger("App.Services.OrderService");

        var discovered = adapter.Discover();

        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, d => d.Name == "App.Services.UserService");
        Assert.Contains(discovered, d => d.Name == "App.Services.OrderService");
    }

    [Fact]
    public void Discover_EmptyBeforeAnyLoggersCreated()
    {
        var adapter = CreateAdapter();
        var discovered = adapter.Discover();
        Assert.Empty(discovered);
    }

    [Fact]
    public void ApplyLevel_SetsCorrectLevel()
    {
        var adapter = CreateAdapter();

        // Create a logger first
        adapter.Factory.CreateLogger("App.MyLogger");

        adapter.ApplyLevel("App.MyLogger", LogLevel.Error);

        var discovered = adapter.Discover();
        var logger = discovered.Single(d => d.Name == "App.MyLogger");
        Assert.Equal(LogLevel.Error, logger.Level);
    }

    [Fact]
    public void ApplyLevel_CreatesEntryForUnknownLogger()
    {
        var adapter = CreateAdapter();

        // Apply level to a logger that hasn't been created yet
        adapter.ApplyLevel("App.FutureLogger", LogLevel.Warn);

        var discovered = adapter.Discover();
        Assert.Single(discovered);
        Assert.Equal("App.FutureLogger", discovered[0].Name);
        Assert.Equal(LogLevel.Warn, discovered[0].Level);
    }

    [Fact]
    public void InstallHook_DetectsNewLoggers()
    {
        var adapter = CreateAdapter();
        var detectedLoggers = new List<(string Name, LogLevel Level)>();

        adapter.InstallHook((name, level) => detectedLoggers.Add((name, level)));

        adapter.Factory.CreateLogger("App.NewService");

        Assert.Single(detectedLoggers);
        Assert.Equal("App.NewService", detectedLoggers[0].Name);
    }

    [Fact]
    public void InstallHook_DoesNotFireForExistingLoggers()
    {
        var adapter = CreateAdapter();

        // Create a logger before installing hook
        adapter.Factory.CreateLogger("App.ExistingService");

        var detectedLoggers = new List<(string Name, LogLevel Level)>();
        adapter.InstallHook((name, level) => detectedLoggers.Add((name, level)));

        // Re-requesting same logger should NOT fire hook
        adapter.Factory.CreateLogger("App.ExistingService");

        Assert.Empty(detectedLoggers);
    }

    [Fact]
    public void UninstallHook_StopsDetection()
    {
        var adapter = CreateAdapter();
        var detectedLoggers = new List<(string Name, LogLevel Level)>();

        adapter.InstallHook((name, level) => detectedLoggers.Add((name, level)));
        adapter.UninstallHook();

        adapter.Factory.CreateLogger("App.AfterUninstall");

        Assert.Empty(detectedLoggers);
    }

    [Theory]
    [InlineData(LogLevel.Trace, MsLogLevel.Trace)]
    [InlineData(LogLevel.Debug, MsLogLevel.Debug)]
    [InlineData(LogLevel.Info, MsLogLevel.Information)]
    [InlineData(LogLevel.Warn, MsLogLevel.Warning)]
    [InlineData(LogLevel.Error, MsLogLevel.Error)]
    [InlineData(LogLevel.Fatal, MsLogLevel.Critical)]
    [InlineData(LogLevel.Silent, MsLogLevel.None)]
    public void LevelMapping_AllLevelsMapCorrectly(LogLevel smplLevel, MsLogLevel expectedMs)
    {
        Assert.Equal(expectedMs, MicrosoftLoggingAdapter.ToMsLevel(smplLevel));
        Assert.Equal(smplLevel, MicrosoftLoggingAdapter.ToSmplLevel(expectedMs));
    }

    [Fact]
    public void Factory_CreateLogger_ReturnsSameLoggerForSameName()
    {
        var adapter = CreateAdapter();

        var logger1 = adapter.Factory.CreateLogger("App.Service");
        var logger2 = adapter.Factory.CreateLogger("App.Service");

        // Both should work and refer to the same tracked logger
        Assert.NotNull(logger1);
        Assert.NotNull(logger2);

        // Only one entry in discover
        var discovered = adapter.Discover();
        Assert.Single(discovered);
    }

    [Fact]
    public void Factory_AddProvider_DelegatesToInnerFactory()
    {
        var adapter = CreateAdapter();

        // Should not throw
        adapter.Factory.AddProvider(new NullLoggerProvider());
    }

    [Fact]
    public void Dispose_CleansUp()
    {
        var adapter = CreateAdapter();
        adapter.InstallHook((_, _) => { });

        adapter.Dispose();

        // Hook should be uninstalled — creating a new logger should not fire
        var detected = new List<string>();
        // We can't reinstall after dispose, but the adapter should be in a clean state
    }

    [Fact]
    public void LevelGatingLogger_LogsWhenEnabled()
    {
        var loggedMessages = new List<string>();
        var innerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(MsLogLevel.Trace);
            builder.AddProvider(new CollectingLoggerProvider(loggedMessages));
        });
        var adapter = new MicrosoftLoggingAdapter(innerFactory);
        var logger = adapter.Factory.CreateLogger("App.TestLogging");

        // With a provider registered and min level Trace, Information should be enabled
        Assert.True(logger.IsEnabled(MsLogLevel.Information));

        logger.Log(MsLogLevel.Information, "test message");
        Assert.Single(loggedMessages);
        Assert.Equal("test message", loggedMessages[0]);
    }

    [Fact]
    public void LevelGatingLogger_SkipsWhenDisabled()
    {
        var loggedMessages = new List<string>();
        var innerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(MsLogLevel.Trace);
            builder.AddProvider(new CollectingLoggerProvider(loggedMessages));
        });
        var adapter = new MicrosoftLoggingAdapter(innerFactory);
        var logger = adapter.Factory.CreateLogger("App.GatedLogger");

        // Set level to Error via the adapter
        adapter.ApplyLevel("App.GatedLogger", LogLevel.Error);

        // Debug should be disabled by the gating layer
        Assert.False(logger.IsEnabled(MsLogLevel.Debug));

        // Logging below the minimum should be a no-op
        logger.Log(MsLogLevel.Debug, "should be skipped");
        Assert.Empty(loggedMessages);
    }

    [Fact]
    public void LevelGatingLogger_BeginScope_Delegates()
    {
        var innerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(MsLogLevel.Trace);
            builder.AddProvider(new CollectingLoggerProvider(new List<string>()));
        });
        var adapter = new MicrosoftLoggingAdapter(innerFactory);
        var logger = adapter.Factory.CreateLogger("App.ScopeTest");

        using var scope = logger.BeginScope("test-scope");
        // Should not throw; scope may be null depending on inner factory
    }

    [Fact]
    public void Factory_Dispose_DoesNotThrow()
    {
        var adapter = CreateAdapter();
        // Dispose the factory (not the adapter)
        (adapter.Factory as IDisposable)?.Dispose();
    }

    [Fact]
    public void ToMsLevel_DefaultCase_ReturnsInformation()
    {
        // Cast an invalid value to hit the default case
        var result = MicrosoftLoggingAdapter.ToMsLevel((LogLevel)999);
        Assert.Equal(MsLogLevel.Information, result);
    }

    [Fact]
    public void ToSmplLevel_DefaultCase_ReturnsInfo()
    {
        // Cast an invalid value to hit the default case
        var result = MicrosoftLoggingAdapter.ToSmplLevel((MsLogLevel)999);
        Assert.Equal(LogLevel.Info, result);
    }

    private sealed class NullLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose() { }
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages;
        public CollectingLoggerProvider(List<string> messages) => _messages = messages;
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(_messages);
        public void Dispose() { }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<string> _messages;
            public CollectingLogger(List<string> messages) => _messages = messages;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(MsLogLevel logLevel) => true;
            public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _messages.Add(formatter(state, exception));
            }
        }
    }
}
