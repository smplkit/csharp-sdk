using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Smplkit;
using Smplkit.Logging;
using Smplkit.Logging.Adapters;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Logging.Adapters;

public class SmplkitLoggingBuilderExtensionsTests
{
    private static SmplClient MakeClient()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/vnd.api+json"),
            }));
        return new SmplClient(TestData.DefaultOptions(), new HttpClient(handler));
    }

    [Fact]
    public void AddSmplkit_NullBuilder_Throws()
    {
        using var client = MakeClient();
        Assert.Throws<ArgumentNullException>(() =>
            SmplkitLoggingBuilderExtensions.AddSmplkit(null!, client));
    }

    [Fact]
    public void AddSmplkit_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LoggerFactory.Create(b => b.AddSmplkit(null!)));
    }

    [Fact]
    public void AddSmplkit_RegistersAllThreeDIServices()
    {
        using var client = MakeClient();
        IServiceCollection? capturedServices = null;

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            capturedServices = builder.Services;
            builder.AddSmplkit(client);
        });

        Assert.NotNull(capturedServices);
        var sp = capturedServices!.BuildServiceProvider();

        // All three DI registrations point to the same MicrosoftLoggingAdapter instance.
        var asProvider = sp.GetServices<ILoggerProvider>().OfType<MicrosoftLoggingAdapter>().Single();
        var asConfigure = sp.GetServices<IConfigureOptions<LoggerFilterOptions>>().OfType<MicrosoftLoggingAdapter>().Single();
        var asTokenSource = sp.GetServices<IOptionsChangeTokenSource<LoggerFilterOptions>>().OfType<MicrosoftLoggingAdapter>().Single();

        Assert.Same(asProvider, asConfigure);
        Assert.Same(asProvider, asTokenSource);
    }

    [Fact]
    public void AddSmplkit_AlsoRegistersAdapterWithLoggingClient()
    {
        using var client = MakeClient();

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddSmplkit(client));

        // Adapter is now registered with client.Logging too — Discover should
        // round-trip whatever the host's CreateLogger calls produced.
        loggerFactory.CreateLogger("Test.Category.A");
        loggerFactory.CreateLogger("Test.Category.B");

        var adaptersField = typeof(LoggingClient)
            .GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var list = (List<ILoggingAdapter>)adaptersField.GetValue(client.Logging)!;
        var adapter = (MicrosoftLoggingAdapter)list.Single();

        var discovered = adapter.Discover().Select(d => d.Name).ToHashSet();
        Assert.Contains("Test.Category.A", discovered);
        Assert.Contains("Test.Category.B", discovered);
    }

    [Fact]
    public void AddSmplkit_ApplyLevelTriggersFilterReload()
    {
        using var client = MakeClient();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
            builder.AddSmplkit(client);
        });

        var adaptersField = typeof(LoggingClient)
            .GetField("_adapters", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var adapter = (MicrosoftLoggingAdapter)((List<ILoggingAdapter>)adaptersField.GetValue(client.Logging)!).Single();

        // Without applying a level, host's SetMinimumLevel(Trace) governs.
        var preLogger = loggerFactory.CreateLogger("Reload.Cat");
        Assert.True(preLogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug));

        // Apply Error — token fires, MEL refreshes filter rules.
        adapter.ApplyLevel("Reload.Cat", LogLevel.Error);

        // Re-create the logger to pick up the refreshed filter (MEL caches loggers
        // until filter options change, which the token has just signalled).
        var postLogger = loggerFactory.CreateLogger("Reload.Cat");
        Assert.False(postLogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug));
        Assert.True(postLogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Error));
    }
}
