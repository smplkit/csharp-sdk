using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Internal;

public class MetricsInstrumentationTests
{
    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };

    private static string SimpleFlagListJson(string key = "my-flag") =>
        $$"""
        {
            "data": [
                {
                    "id": "flag-001",
                    "type": "flag",
                    "attributes": {
                        "key": "{{key}}",
                        "name": "Test Flag",
                        "type": "BOOLEAN",
                        "default": true,
                        "values": [],
                        "description": null,
                        "environments": {
                            "test": {
                                "enabled": true,
                                "default": null,
                                "rules": []
                            }
                        },
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static MetricsReporter CreateSeparateReporter(out MockHttpMessageHandler metricsHandler)
    {
        metricsHandler = new MockHttpMessageHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var metricsClient = new HttpClient(metricsHandler);
        return new MetricsReporter(metricsClient, "test", "test-service");
    }

    private static SmplClient CreateClientWithReporter(
        MetricsReporter reporter,
        string flagJson)
    {
        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(flagJson)));
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        return new SmplClient(options, httpClient);
    }

    private static void InjectMetrics(object target, MetricsReporter reporter)
    {
        var field = target.GetType().GetField("_metrics",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field!.SetValue(target, reporter);
    }

    private static async Task<JsonElement> GetPayload(MockHttpMessageHandler handler, int index = -1)
    {
        var requests = handler.Requests
            .Where(r => r.RequestUri?.PathAndQuery.Contains("metrics") == true).ToList();
        var req = index < 0 ? requests.Last() : requests[index];
        var body = await req.Content!.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    // ------------------------------------------------------------------
    // Flags instrumentation
    // ------------------------------------------------------------------

    [Fact]
    public async Task FlagsEvaluation_RecordsCacheMiss_OnFirstEval()
    {
        var reporter = CreateSeparateReporter(out var metricsHandler);
        var client = CreateClientWithReporter(reporter, SimpleFlagListJson());
        InjectMetrics(client.Flags, reporter);

        var handle = client.Flags.BooleanFlag("my-flag", false);
        handle.Get(); // First eval = cache miss

        reporter.Flush();

        var payload = await GetPayload(metricsHandler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();

        var metricNames = entries.Select(e =>
            e.GetProperty("attributes").GetProperty("name").GetString()).ToList();
        Assert.Contains("flags.evaluations", metricNames);
        Assert.Contains("flags.cache_misses", metricNames);
        Assert.DoesNotContain("flags.cache_hits", metricNames);

        reporter.Dispose();
    }

    [Fact]
    public async Task FlagsEvaluation_RecordsCacheHit_OnSecondEval()
    {
        var reporter = CreateSeparateReporter(out var metricsHandler);
        var client = CreateClientWithReporter(reporter, SimpleFlagListJson());
        InjectMetrics(client.Flags, reporter);

        var handle = client.Flags.BooleanFlag("my-flag", false);
        handle.Get(); // cache miss
        handle.Get(); // cache hit

        reporter.Flush();

        var payload = await GetPayload(metricsHandler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();

        var metricNames = entries.Select(e =>
            e.GetProperty("attributes").GetProperty("name").GetString()).ToList();
        Assert.Contains("flags.cache_hits", metricNames);
        Assert.Contains("flags.cache_misses", metricNames);
        Assert.Contains("flags.evaluations", metricNames);

        var evalEntries = entries.Where(e =>
            e.GetProperty("attributes").GetProperty("name").GetString() == "flags.evaluations").ToList();
        var totalEvals = evalEntries.Sum(e =>
            e.GetProperty("attributes").GetProperty("value").GetInt32());
        Assert.Equal(2, totalEvals);

        reporter.Dispose();
    }

    [Fact]
    public async Task FlagsEvaluation_IncludesFlagIdDimension()
    {
        var reporter = CreateSeparateReporter(out var metricsHandler);
        var client = CreateClientWithReporter(reporter, SimpleFlagListJson("checkout-v2"));
        InjectMetrics(client.Flags, reporter);

        var handle = client.Flags.BooleanFlag("checkout-v2", false);
        handle.Get();

        reporter.Flush();

        var payload = await GetPayload(metricsHandler);
        var evalEntry = payload.GetProperty("data").EnumerateArray()
            .First(e => e.GetProperty("attributes").GetProperty("name").GetString() == "flags.evaluations");
        var dims = evalEntry.GetProperty("attributes").GetProperty("dimensions");
        Assert.Equal("checkout-v2", dims.GetProperty("flag_id").GetString());

        reporter.Dispose();
    }

    // ------------------------------------------------------------------
    // Config instrumentation
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConfigResolve_RecordsResolution()
    {
        var reporter = CreateSeparateReporter(out var metricsHandler);

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(TestData.ConfigListJson())));
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplClient(options, httpClient);
        InjectMetrics(client.Config, reporter);

        var resolved = client.Config.Resolve("user_service");
        Assert.NotNull(resolved);

        reporter.Flush();

        var payload = await GetPayload(metricsHandler);
        var entries = payload.GetProperty("data").EnumerateArray().ToList();

        var resolutionEntry = entries.First(e =>
            e.GetProperty("attributes").GetProperty("name").GetString() == "config.resolutions");
        Assert.Equal(1, resolutionEntry.GetProperty("attributes").GetProperty("value").GetInt32());
        var dims = resolutionEntry.GetProperty("attributes").GetProperty("dimensions");
        Assert.Equal("user_service", dims.GetProperty("config_id").GetString());

        reporter.Dispose();
    }

    [Fact]
    public async Task ConfigChange_RecordsChanges()
    {
        var reporter = CreateSeparateReporter(out var metricsHandler);

        var handler = new MockHttpMessageHandler(_ =>
            Task.FromResult(JsonResponse(TestData.ConfigListJson())));
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplClient(options, httpClient);
        InjectMetrics(client.Config, reporter);

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["my-config"] = new() { ["timeout"] = 30 },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["my-config"] = new() { ["timeout"] = 60 },
        };

        client.Config.DiffAndFire(oldCache, newCache, "test");

        reporter.Flush();

        var payload = await GetPayload(metricsHandler);
        var changeEntry = payload.GetProperty("data").EnumerateArray()
            .First(e => e.GetProperty("attributes").GetProperty("name").GetString() == "config.changes");
        Assert.Equal(1, changeEntry.GetProperty("attributes").GetProperty("value").GetInt32());
        var dims = changeEntry.GetProperty("attributes").GetProperty("dimensions");
        Assert.Equal("my-config", dims.GetProperty("config_id").GetString());

        reporter.Dispose();
    }

    // ------------------------------------------------------------------
    // WebSocket instrumentation
    // ------------------------------------------------------------------

    [Fact]
    public async Task WebSocket_RecordsConnectionGauge()
    {
        var reporter = CreateSeparateReporter(out var metricsHandler);

        reporter.RecordGauge("platform.websocket_connections", 1, unit: "connections");
        reporter.RecordGauge("platform.websocket_connections", 0, unit: "connections");
        reporter.Flush();

        var payload = await GetPayload(metricsHandler);
        var entry = payload.GetProperty("data").EnumerateArray().First();

        Assert.Equal("platform.websocket_connections",
            entry.GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal(0, entry.GetProperty("attributes").GetProperty("value").GetInt32());
        Assert.Equal("connections", entry.GetProperty("attributes").GetProperty("unit").GetString());

        reporter.Dispose();
    }

    // ------------------------------------------------------------------
    // End-to-end: SmplClient wiring
    // ------------------------------------------------------------------

    [Fact]
    public void SmplClient_TelemetryEnabled_MetricsFlushOnDispose()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new MockHttpMessageHandler(req =>
        {
            requests.Add(req);
            return Task.FromResult(JsonResponse(SimpleFlagListJson()));
        });
        var httpClient = new HttpClient(handler);
        var options = new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
        };
        var client = new SmplClient(options, httpClient);

        var handle = client.Flags.BooleanFlag("my-flag", false);
        handle.Get();

        client.Dispose();

        var metricsRequests = requests
            .Where(r => r.RequestUri?.PathAndQuery.Contains("metrics/bulk") == true).ToList();
        Assert.NotEmpty(metricsRequests);
    }

    [Fact]
    public void SmplClient_TelemetryDisabled_NoMetricsRequests()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new MockHttpMessageHandler(req =>
        {
            requests.Add(req);
            return Task.FromResult(JsonResponse(SimpleFlagListJson()));
        });
        var httpClient = new HttpClient(handler);
        var options = new SmplClientOptions
        {
            ApiKey = "sk_api_test_key",
            Environment = "test",
            Service = "test-service",
            DisableTelemetry = true,
        };
        var client = new SmplClient(options, httpClient);

        var handle = client.Flags.BooleanFlag("my-flag", false);
        handle.Get();
        client.Dispose();

        var metricsRequests = requests
            .Where(r => r.RequestUri?.PathAndQuery.Contains("metrics/bulk") == true).ToList();
        Assert.Empty(metricsRequests);
    }
}
