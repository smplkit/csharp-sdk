using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smplkit;
using Smplkit.Logging;
using Smplkit.Logging.Adapters;
using Smplkit.Tests.Helpers;
using Xunit;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Smplkit.Tests.Logging.Adapters;

public class MicrosoftLoggingAdapterTests
{
    [Fact]
    public void Name_ReturnsMicrosoftLogging()
    {
        var adapter = new MicrosoftLoggingAdapter();
        Assert.Equal("microsoft-logging", adapter.Name);
    }

    [Fact]
    public void IOptionsChangeTokenSourceName_IsDefault()
    {
        var adapter = new MicrosoftLoggingAdapter();
        Assert.Equal(Options.DefaultName, ((IOptionsChangeTokenSource<LoggerFilterOptions>)adapter).Name);
    }

    [Fact]
    public void Discover_EmptyBeforeAnyLoggersCreated()
    {
        var adapter = new MicrosoftLoggingAdapter();
        Assert.Empty(adapter.Discover());
    }

    [Fact]
    public void CreateLogger_CapturesCategoryAndReturnsPassThroughLogger()
    {
        var adapter = new MicrosoftLoggingAdapter();
        var logger = adapter.CreateLogger("App.Foo");

        // The returned logger reports IsEnabled=true so MEL's host filter rules
        // (the ones we contribute via Configure) govern; Log itself is a no-op
        // because we are a level controller, not a sink.
        Assert.True(logger.IsEnabled(MsLogLevel.Trace));
        Assert.True(logger.IsEnabled(MsLogLevel.Critical));
        // And it is NOT the NullLogger sentinel (which some host paths skip).
        Assert.NotSame(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, logger);

        // Log + BeginScope are no-ops by design; just exercise both.
        logger.Log(MsLogLevel.Information, new EventId(0), state: 0, exception: null, formatter: (_, _) => "x");
        var scope = logger.BeginScope("scope-state");
        Assert.Null(scope);

        var discovered = adapter.Discover();
        Assert.Single(discovered);
        Assert.Equal("App.Foo", discovered[0].Name);
        Assert.Equal(LogLevel.Trace, discovered[0].Level); // unrestricted until ApplyLevel
    }

    [Fact]
    public void CreateLogger_DedupsByCategory()
    {
        var adapter = new MicrosoftLoggingAdapter();
        adapter.CreateLogger("App.Foo");
        adapter.CreateLogger("App.Foo");
        adapter.CreateLogger("App.Foo");
        Assert.Single(adapter.Discover());
    }

    [Fact]
    public void ApplyLevel_ReportsBackOnDiscover()
    {
        var adapter = new MicrosoftLoggingAdapter();
        adapter.CreateLogger("App.Foo");
        adapter.ApplyLevel("App.Foo", LogLevel.Error);

        var entry = adapter.Discover().Single();
        Assert.Equal(LogLevel.Error, entry.Level);
    }

    [Fact]
    public void ApplyLevel_ForUnknownCategory_RegistersIt()
    {
        var adapter = new MicrosoftLoggingAdapter();
        adapter.ApplyLevel("App.Future", LogLevel.Warn);

        var discovered = adapter.Discover();
        Assert.Single(discovered);
        Assert.Equal("App.Future", discovered[0].Name);
        Assert.Equal(LogLevel.Warn, discovered[0].Level);
    }

    [Fact]
    public void Configure_EmitsRulesForAppliedCategoriesOnly()
    {
        var adapter = new MicrosoftLoggingAdapter();
        adapter.CreateLogger("App.Untouched"); // captured but no level applied
        adapter.CreateLogger("App.Managed");
        adapter.ApplyLevel("App.Managed", LogLevel.Warn);

        var options = new LoggerFilterOptions();
        ((IConfigureOptions<LoggerFilterOptions>)adapter).Configure(options);

        Assert.Single(options.Rules);
        var rule = options.Rules[0];
        Assert.Null(rule.ProviderName); // applies to every provider
        Assert.Equal("App.Managed", rule.CategoryName);
        Assert.Equal(MsLogLevel.Warning, rule.LogLevel);
    }

    [Fact]
    public void GetChangeToken_FiresWhenApplyLevelCalled()
    {
        var adapter = new MicrosoftLoggingAdapter();
        var token = ((IOptionsChangeTokenSource<LoggerFilterOptions>)adapter).GetChangeToken();
        Assert.False(token.HasChanged);

        adapter.ApplyLevel("App.Foo", LogLevel.Info);

        Assert.True(token.HasChanged);
    }

    [Fact]
    public void GetChangeToken_RotatesAfterTrigger()
    {
        var adapter = new MicrosoftLoggingAdapter();
        var t1 = ((IOptionsChangeTokenSource<LoggerFilterOptions>)adapter).GetChangeToken();
        adapter.ApplyLevel("a", LogLevel.Info);
        var t2 = ((IOptionsChangeTokenSource<LoggerFilterOptions>)adapter).GetChangeToken();

        Assert.True(t1.HasChanged);
        Assert.False(t2.HasChanged);

        adapter.ApplyLevel("b", LogLevel.Info);
        Assert.True(t2.HasChanged);
    }

    [Fact]
    public void InstallHook_FiresOnNewCategories()
    {
        var adapter = new MicrosoftLoggingAdapter();
        var detected = new List<string>();
        adapter.InstallHook((name, _) => detected.Add(name));

        adapter.CreateLogger("App.New");

        Assert.Single(detected);
        Assert.Equal("App.New", detected[0]);
    }

    [Fact]
    public void InstallHook_DoesNotFireForExistingCategories()
    {
        var adapter = new MicrosoftLoggingAdapter();
        adapter.CreateLogger("App.PreExisting");

        var detected = new List<string>();
        adapter.InstallHook((name, _) => detected.Add(name));

        adapter.CreateLogger("App.PreExisting"); // already known
        Assert.Empty(detected);
    }

    [Fact]
    public void UninstallHook_StopsNotifications()
    {
        var adapter = new MicrosoftLoggingAdapter();
        var detected = new List<string>();
        adapter.InstallHook((name, _) => detected.Add(name));
        adapter.UninstallHook();

        adapter.CreateLogger("App.AfterUninstall");
        Assert.Empty(detected);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var adapter = new MicrosoftLoggingAdapter();
        adapter.Dispose();
        adapter.Dispose(); // should not throw
    }

    [Theory]
    [InlineData(LogLevel.Trace, MsLogLevel.Trace)]
    [InlineData(LogLevel.Debug, MsLogLevel.Debug)]
    [InlineData(LogLevel.Info, MsLogLevel.Information)]
    [InlineData(LogLevel.Warn, MsLogLevel.Warning)]
    [InlineData(LogLevel.Error, MsLogLevel.Error)]
    [InlineData(LogLevel.Fatal, MsLogLevel.Critical)]
    [InlineData(LogLevel.Silent, MsLogLevel.None)]
    public void LevelMapping_RoundtripsCorrectly(LogLevel smplLevel, MsLogLevel expectedMs)
    {
        Assert.Equal(expectedMs, MicrosoftLoggingAdapter.ToMsLevel(smplLevel));
        Assert.Equal(smplLevel, MicrosoftLoggingAdapter.ToSmplLevel(expectedMs));
    }

    [Fact]
    public void ToMsLevel_DefaultCase_ReturnsInformation()
    {
        Assert.Equal(MsLogLevel.Information, MicrosoftLoggingAdapter.ToMsLevel((LogLevel)999));
    }

    [Fact]
    public void ToSmplLevel_DefaultCase_ReturnsInfo()
    {
        Assert.Equal(LogLevel.Info, MicrosoftLoggingAdapter.ToSmplLevel((MsLogLevel)999));
    }

    [Fact]
    public void EndToEnd_AppliedLevelGatesAllProviders()
    {
        // Build a real LoggerFactory with our adapter in the pipeline alongside
        // a collecting provider. Apply a Warning level for "App.Db" and verify
        // the collector does NOT receive Information events for that category.
        var collected = new List<(string Category, MsLogLevel Level, string Message)>();
        var collectingProvider = new CollectingLoggerProvider(collected);

        // Construct a SmplClient just so AddSmplkit has something to attach to.
        // We don't call InstallAsync — the adapter functions independently of
        // the runtime client for this filter-pipeline test.
        var http = new HttpClient(new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
            })));
        using var client = new SmplClient(TestData.DefaultOptions(), http);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(MsLogLevel.Trace);
            builder.AddProvider(collectingProvider);
            builder.AddSmplkit(client);
        });

        // Find the registered adapter via the smplkit client.
        var adaptersField = typeof(LoggingClient)
            .GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var list = (List<ILoggingAdapter>)adaptersField.GetValue(client.Logging)!;
        var adapter = (MicrosoftLoggingAdapter)list.Single();

        // Apply Warning level via the adapter — this should propagate through
        // LoggerFilterOptions to gate ALL providers.
        adapter.ApplyLevel("App.Db", LogLevel.Warn);

        var dbLogger = loggerFactory.CreateLogger("App.Db");
        dbLogger.Log(MsLogLevel.Information, "info-event"); // below Warning → dropped
        dbLogger.Log(MsLogLevel.Error, "error-event");      // above Warning → emitted

        Assert.DoesNotContain(collected, e => e.Message == "info-event");
        Assert.Contains(collected, e => e.Message == "error-event" && e.Level == MsLogLevel.Error);
    }

    [Fact]
    public void EndToEnd_UnmanagedCategory_RespectsHostFilters()
    {
        // A category that the adapter has not been told about should follow
        // the host's existing minimum-level configuration, not be silently
        // gated by us.
        var collected = new List<(string Category, MsLogLevel Level, string Message)>();
        var collectingProvider = new CollectingLoggerProvider(collected);

        var http = new HttpClient(new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/vnd.api+json"),
            })));
        using var client = new SmplClient(TestData.DefaultOptions(), http);

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(MsLogLevel.Information);
            builder.AddProvider(collectingProvider);
            builder.AddSmplkit(client);
        });

        var orphan = loggerFactory.CreateLogger("App.Unmanaged");
        orphan.Log(MsLogLevel.Debug, "debug-event");      // below host's Information → dropped
        orphan.Log(MsLogLevel.Information, "info-event"); // at host's level → emitted

        Assert.DoesNotContain(collected, e => e.Message == "debug-event");
        Assert.Contains(collected, e => e.Message == "info-event");
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, MsLogLevel Level, string Message)> _events;
        public CollectingLoggerProvider(List<(string, MsLogLevel, string)> events) => _events = events;
        public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, _events);
        public void Dispose() { }

        private sealed class CollectingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<(string Category, MsLogLevel Level, string Message)> _events;
            public CollectingLogger(string category, List<(string, MsLogLevel, string)> events)
            {
                _category = category;
                _events = events;
            }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(MsLogLevel logLevel) => true;
            public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                _events.Add((_category, logLevel, formatter(state, exception)));
            }
        }
    }
}
