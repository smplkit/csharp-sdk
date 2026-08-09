using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Internal;
using Smplkit.Logging;
using Smplkit.Logging.Adapters;
using Smplkit.Tests.Helpers;
using Xunit;
using LoggingClient = Smplkit.Logging.LoggingClient;

namespace Smplkit.Tests.Logging;

/// <summary>
/// Tests for the stateless logging mode (<c>streaming: false</c>):
/// InstallAsync still hooks adapters and applies levels once, but opens no
/// event stream and starts no periodic flush timer; adapter-discovered loggers
/// past the threshold flush inline; RefreshAsync re-fetches on demand.
/// </summary>
public class LoggingStatelessTests
{
    private static readonly Func<EventStream> ThrowingEvents =
        () => throw new InvalidOperationException("stateless mode must not create an event stream");

    private static (LoggingClient logging, MockHttpMessageHandler handler) MakeStateless(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var factory = new GeneratedClientFactory(http, new SmplClientOptions
        {
            ApiKey = TestData.ApiKey,
            BaseDomain = "example.test",
        });
        var logging = new LoggingClient(factory, TestData.ApiKey, ThrowingEvents, parent: null, metrics: null, streaming: false);
        return (logging, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static bool IsLoggersBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("/loggers/bulk");

    private const string LoggerListJson = """
        {
            "data": [
                {
                    "id": "billing",
                    "type": "logger",
                    "attributes": {
                        "name": "billing",
                        "level": "WARN",
                        "group": null,
                        "managed": true,
                        "sources": [],
                        "environments": {}
                    }
                }
            ]
        }
        """;

    private sealed class RecordingAdapter : ILoggingAdapter
    {
        private Action<string, LogLevel>? _hook;
        public string Name => "recording";
        public List<(string Logger, LogLevel Level)> AppliedLevels { get; } = new();
        public IReadOnlyList<DiscoveredLogger> Discover() => Array.Empty<DiscoveredLogger>();
        public void InstallHook(Action<string, LogLevel> callback) => _hook = callback;
        public void UninstallHook() => _hook = null;
        public void ApplyLevel(string loggerName, LogLevel level) => AppliedLevels.Add((loggerName, level));
        public void TriggerHook(string name, LogLevel level) => _hook?.Invoke(name, level);
    }

    private static object? Field(LoggingClient logging, string name)
        => typeof(LoggingClient).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(logging);

    [Fact]
    public async Task InstallAsync_Stateless_NoEventStreamNoTimer_AppliesLevelsOnce()
    {
        var (logging, _) = MakeStateless(req =>
            Task.FromResult(Json(req.RequestUri!.AbsolutePath.Contains("/log_groups")
                ? TestData.EmptyListJson()
                : LoggerListJson)));
        var adapter = new RecordingAdapter();
        logging.RegisterAdapter(adapter);

        await logging.InstallAsync();

        // Levels were fetched and applied once...
        Assert.Contains(("billing", LogLevel.Warn), adapter.AppliedLevels);
        // ...with no live machinery: no event stream, no periodic flush timer.
        Assert.Null(Field(logging, "_eventStream"));
        Assert.Null(Field(logging, "_loggerFlushTimer"));

        // The live surface is installed: OnChange registers without throwing.
        logging.OnChange(_ => { });
        logging.Dispose();
    }

    [Fact]
    public async Task AdapterHook_Threshold_Stateless_FlushesInline()
    {
        var (logging, handler) = MakeStateless(req =>
            Task.FromResult(Json(req.Method == HttpMethod.Get ? TestData.EmptyListJson() : "{}")));
        var adapter = new RecordingAdapter();
        logging.RegisterAdapter(adapter);
        await logging.InstallAsync();
        var bulkPostsAfterInstall = handler.Requests.Count(IsLoggersBulkPost);

        for (int i = 0; i < 50; i++)
            adapter.TriggerHook($"logger-{i}", LogLevel.Info);

        // The threshold flush ran inline — the bulk POST is already recorded
        // and the task is complete before the hook callback returns.
        Assert.True(handler.Requests.Count(IsLoggersBulkPost) > bulkPostsAfterInstall);
        Assert.NotNull(logging._lastLoggerBufferFlushTask);
        Assert.True(logging._lastLoggerBufferFlushTask!.IsCompleted);
        logging.Dispose();
    }

    [Fact]
    public async Task RefreshAsync_Stateless_RefetchesOnDemand()
    {
        int loggerGets = 0;
        var (logging, _) = MakeStateless(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/loggers"))
                Interlocked.Increment(ref loggerGets);
            return Task.FromResult(Json(req.RequestUri!.AbsolutePath.Contains("/log_groups")
                ? TestData.EmptyListJson()
                : LoggerListJson));
        });
        var adapter = new RecordingAdapter();
        logging.RegisterAdapter(adapter);
        await logging.InstallAsync();
        var getsAfterInstall = loggerGets;

        await logging.RefreshAsync();

        Assert.True(loggerGets > getsAfterInstall);
        logging.Dispose();
    }
}
