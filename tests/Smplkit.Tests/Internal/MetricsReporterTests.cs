using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Internal;

public class MetricsReporterTests
{
    private const string Environment = "test";
    private const string Service = "test-service";

    private static (MetricsReporter reporter, MockHttpMessageHandler handler) CreateReporter(
        int flushIntervalSeconds = 3600)
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);
        var reporter = new MetricsReporter(httpClient, Environment, Service, flushIntervalSeconds);
        return (reporter, handler);
    }

    // ------------------------------------------------------------------
    // Counter accumulation
    // ------------------------------------------------------------------

    [Fact]
    public void Record_AccumulatesValues_SameKey()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("flags.evaluations", unit: "evaluations");
        reporter.Record("flags.evaluations", unit: "evaluations");
        reporter.Record("flags.evaluations", unit: "evaluations");
        reporter.Flush();

        Assert.Single(handler.Requests);
        var payload = ParsePayload(handler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal(3, entries[0].GetProperty("attributes").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Record_SeparateCounters_DifferentDimensions()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("flags.evaluations", unit: "evaluations",
            dimensions: new Dictionary<string, string> { ["flag_id"] = "flag-a" });
        reporter.Record("flags.evaluations", unit: "evaluations",
            dimensions: new Dictionary<string, string> { ["flag_id"] = "flag-b" });
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e =>
            Assert.Equal(1, e.GetProperty("attributes").GetProperty("value").GetInt32()));
    }

    [Fact]
    public void Record_WithExplicitValue_Accumulates()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("logging.loggers_discovered", value: 5, unit: "loggers");
        reporter.Record("logging.loggers_discovered", value: 3, unit: "loggers");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entry = payload.GetProperty("data").EnumerateArray().First();
        Assert.Equal(8, entry.GetProperty("attributes").GetProperty("value").GetInt32());
    }

    [Fact]
    public void Record_UnitFirstWriteWins()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric", unit: "evaluations");
        reporter.Record("test.metric", unit: "ignored-unit");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entry = payload.GetProperty("data").EnumerateArray().First();
        Assert.Equal("evaluations", entry.GetProperty("attributes").GetProperty("unit").GetString());
    }

    [Fact]
    public void Record_UnitSetLater_IfFirstWasNull()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Record("test.metric", unit: "hits");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entry = payload.GetProperty("data").EnumerateArray().First();
        Assert.Equal("hits", entry.GetProperty("attributes").GetProperty("unit").GetString());
    }

    // ------------------------------------------------------------------
    // Gauge behavior
    // ------------------------------------------------------------------

    [Fact]
    public void RecordGauge_ReplacesValue()
    {
        var (reporter, handler) = CreateReporter();

        reporter.RecordGauge("platform.websocket_connections", 1, unit: "connections");
        reporter.RecordGauge("platform.websocket_connections", 0, unit: "connections");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(entries);
        Assert.Equal(0, entries[0].GetProperty("attributes").GetProperty("value").GetInt32());
    }

    [Fact]
    public void CountersAndGauges_AreSeparateStorage()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric", value: 5, unit: "count");
        reporter.RecordGauge("test.metric", 10, unit: "gauge");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        var values = entries.Select(e => e.GetProperty("attributes").GetProperty("value").GetInt32())
            .OrderBy(v => v).ToList();
        Assert.Equal(5, values[0]);
        Assert.Equal(10, values[1]);
    }

    // ------------------------------------------------------------------
    // Flush behavior
    // ------------------------------------------------------------------

    [Fact]
    public void Flush_EmptyCounters_NoHttpCall()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Flush();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Flush_PostsToCorrectEndpoint()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Flush();

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://app.smplkit.com/api/v1/metrics/bulk", request.RequestUri!.ToString());
    }

    [Fact]
    public void Flush_SendsJsonApiContentType()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Flush();

        var request = handler.Requests[0];
        Assert.Equal("application/vnd.api+json", request.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public void Flush_PayloadShape_MatchesSpec()
    {
        var (reporter, handler) = CreateReporter(flushIntervalSeconds: 60);

        reporter.Record("flags.evaluations", unit: "evaluations",
            dimensions: new Dictionary<string, string> { ["flag_id"] = "checkout-v2" });
        reporter.Flush();

        var payload = ParsePayload(handler);
        Assert.True(payload.TryGetProperty("data", out var data));

        var entry = data.EnumerateArray().First();
        Assert.Equal("metric", entry.GetProperty("type").GetString());

        var attrs = entry.GetProperty("attributes");
        Assert.Equal("flags.evaluations", attrs.GetProperty("name").GetString());
        Assert.Equal(1, attrs.GetProperty("value").GetInt32());
        Assert.Equal("evaluations", attrs.GetProperty("unit").GetString());
        Assert.Equal(60, attrs.GetProperty("period_seconds").GetInt32());

        var dims = attrs.GetProperty("dimensions");
        Assert.Equal(Environment, dims.GetProperty("environment").GetString());
        Assert.Equal(Service, dims.GetProperty("service").GetString());
        Assert.Equal("checkout-v2", dims.GetProperty("flag_id").GetString());

        Assert.True(attrs.TryGetProperty("recorded_at", out var recordedAt));
        Assert.False(string.IsNullOrEmpty(recordedAt.GetString()));
    }

    [Fact]
    public void Flush_DimensionsAlwaysIncludeEnvironmentAndService()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var dims = payload.GetProperty("data").EnumerateArray().First()
            .GetProperty("attributes").GetProperty("dimensions");
        Assert.Equal(Environment, dims.GetProperty("environment").GetString());
        Assert.Equal(Service, dims.GetProperty("service").GetString());
    }

    [Fact]
    public void Flush_ClearsCounters_SecondFlushEmpty()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Flush();
        Assert.Single(handler.Requests);

        reporter.Flush();
        Assert.Single(handler.Requests); // No second request
    }

    [Fact]
    public void Flush_FailedPost_DoesNotThrow()
    {
        var handler = new MockHttpMessageHandler(_ =>
            throw new HttpRequestException("Network error"));
        var httpClient = new HttpClient(handler);
        var reporter = new MetricsReporter(httpClient, Environment, Service);

        reporter.Record("test.metric");

        var ex = Record.Exception(() => reporter.Flush());
        Assert.Null(ex);
    }

    [Fact]
    public void Flush_ServerError_DoesNotThrow()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var httpClient = new HttpClient(handler);
        var reporter = new MetricsReporter(httpClient, Environment, Service);

        reporter.Record("test.metric");

        var ex = Record.Exception(() => reporter.Flush());
        Assert.Null(ex);
    }

    // ------------------------------------------------------------------
    // Timer behavior
    // ------------------------------------------------------------------

    [Fact]
    public async Task Timer_StartsLazily_OnFirstRecord()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);
        var reporter = new MetricsReporter(httpClient, Environment, Service, flushIntervalSeconds: 1);

        // No records yet — no flush
        await Task.Delay(1500);
        Assert.Empty(handler.Requests);

        // Record triggers timer
        reporter.Record("test.metric");
        await Task.Delay(1500);
        Assert.NotEmpty(handler.Requests);

        reporter.Close();
    }

    [Fact]
    public async Task Timer_FlushesAutomatically()
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var httpClient = new HttpClient(handler);
        var reporter = new MetricsReporter(httpClient, Environment, Service, flushIntervalSeconds: 1);

        reporter.Record("test.metric");
        await Task.Delay(1500);

        Assert.NotEmpty(handler.Requests);
        reporter.Close();
    }

    // ------------------------------------------------------------------
    // Close behavior
    // ------------------------------------------------------------------

    [Fact]
    public void Close_PerformsFinalFlush()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Close();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Close_IsIdempotent()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Close();
        reporter.Close();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Dispose_CallsClose()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Dispose();

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Record_AfterClose_StillAccumulatesButNoTimer()
    {
        var (reporter, handler) = CreateReporter();

        reporter.Record("test.metric");
        reporter.Close();
        Assert.Single(handler.Requests);

        // Record after close — accumulates but no new timer
        reporter.Record("another.metric");
        reporter.Flush(); // Manual flush still works
        Assert.Equal(2, handler.Requests.Count);
    }

    // ------------------------------------------------------------------
    // Thread safety
    // ------------------------------------------------------------------

    [Fact]
    public async Task Record_ThreadSafe_ConcurrentAccess()
    {
        var (reporter, handler) = CreateReporter();
        const int threadCount = 10;
        const int recordsPerThread = 100;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < recordsPerThread; i++)
                reporter.Record("concurrent.metric", unit: "ops");
        })).ToArray();

        await Task.WhenAll(tasks);
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entry = payload.GetProperty("data").EnumerateArray().First();
        Assert.Equal(threadCount * recordsPerThread,
            entry.GetProperty("attributes").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task RecordGauge_ThreadSafe_ConcurrentAccess()
    {
        var (reporter, handler) = CreateReporter();

        var tasks = Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            reporter.RecordGauge("concurrent.gauge", i, unit: "value")
        )).ToArray();

        await Task.WhenAll(tasks);
        reporter.Flush();

        // Should have exactly one entry (all go to same key)
        var payload = ParsePayload(handler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(entries);
    }

    [Fact]
    public void RecordGauge_UnitSetLater_IfFirstWasNull()
    {
        var (reporter, handler) = CreateReporter();

        reporter.RecordGauge("test.gauge", 1);
        reporter.RecordGauge("test.gauge", 2, unit: "connections");
        reporter.Flush();

        var payload = ParsePayload(handler);
        var entry = payload.GetProperty("data").EnumerateArray().First();
        Assert.Equal("connections", entry.GetProperty("attributes").GetProperty("unit").GetString());
    }

    [Fact]
    public async Task Timer_RestartsAfterTick_WhenNewRecordsDuringFlush()
    {
        var flushStarted = new ManualResetEventSlim(false);
        var flushGate = new ManualResetEventSlim(false);
        var flushCount = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            var count = Interlocked.Increment(ref flushCount);
            if (count == 1)
            {
                flushStarted.Set(); // Signal that flush has started
                flushGate.Wait();   // Block until we add more records
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var httpClient = new HttpClient(handler);
        var reporter = new MetricsReporter(httpClient, Environment, Service, flushIntervalSeconds: 1);

        reporter.Record("test.metric1");
        // Wait for first timer tick to start flushing (blocked in handler)
        flushStarted.Wait(TimeSpan.FromSeconds(5));
        Assert.Equal(1, flushCount);

        // Record while flush is blocked — after FlushInternal returns,
        // Tick() will find _counters non-empty and restart the timer.
        reporter.Record("test.metric2");
        flushGate.Set();

        // Wait for second timer tick
        await Task.Delay(2000);
        Assert.True(flushCount >= 2);

        reporter.Close();
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static JsonElement ParsePayload(MockHttpMessageHandler handler)
    {
        var request = handler.Requests.Last();
        var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return JsonDocument.Parse(body).RootElement;
    }
}
