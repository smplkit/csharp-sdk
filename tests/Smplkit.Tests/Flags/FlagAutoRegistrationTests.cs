using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Tests for the flag auto-registration pipeline: FlagRegistrationBuffer,
/// FlushFlagsAsync, typed method buffer population, threshold flush, timer, and Close.
/// </summary>
public class FlagAutoRegistrationTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFn)
    {
        var handler = new MockHttpMessageHandler(handlerFn);
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplClient(options, httpClient);
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };
    }

    private static string FlagListJson(string id = "my-flag") =>
        $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "flag",
                    "attributes": {
                        "id": "{{id}}",
                        "name": "My Flag",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [],
                        "description": null,
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;

    private static string EmptyFlagListJson() => """{"data": []}""";

    private static string BulkResponseJson(int registered = 1) =>
        $$"""{"registered": {{registered}}}""";

    // ---------------------------------------------------------------
    // FlagRegistrationBuffer — unit tests
    // ---------------------------------------------------------------

    [Fact]
    public void Buffer_Add_AddsToPending()
    {
        var buffer = new FlagRegistrationBuffer();

        buffer.Add("my-flag", "BOOLEAN", false, "svc", "prod");

        Assert.Equal(1, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_Add_Deduplicates_SameId()
    {
        var buffer = new FlagRegistrationBuffer();

        buffer.Add("my-flag", "BOOLEAN", false, "svc", "prod");
        buffer.Add("my-flag", "BOOLEAN", true, "svc", "prod"); // duplicate id

        Assert.Equal(1, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_Add_MultipleDistinctIds_AllPending()
    {
        var buffer = new FlagRegistrationBuffer();

        buffer.Add("flag-a", "BOOLEAN", false, "svc", "prod");
        buffer.Add("flag-b", "STRING", "hello", "svc", "prod");
        buffer.Add("flag-c", "NUMERIC", 42.0, "svc", "prod");

        Assert.Equal(3, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_Drain_ReturnsPendingItems()
    {
        var buffer = new FlagRegistrationBuffer();
        buffer.Add("flag-a", "BOOLEAN", false, "svc", "prod");
        buffer.Add("flag-b", "STRING", "val", "svc", "prod");

        var batch = buffer.Drain();

        Assert.Equal(2, batch.Count);
        Assert.Contains(batch, e => e.Id == "flag-a" && e.Type == "BOOLEAN");
        Assert.Contains(batch, e => e.Id == "flag-b" && e.Type == "STRING");
    }

    [Fact]
    public void Buffer_Drain_ClearsPending()
    {
        var buffer = new FlagRegistrationBuffer();
        buffer.Add("flag-x", "BOOLEAN", false, "svc", "prod");

        buffer.Drain();

        Assert.Equal(0, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_Drain_EmptyBuffer_ReturnsEmpty()
    {
        var buffer = new FlagRegistrationBuffer();

        var batch = buffer.Drain();

        Assert.Empty(batch);
    }

    [Fact]
    public void Buffer_SeenPersistsAfterDrain_DuplicateNotReAdded()
    {
        var buffer = new FlagRegistrationBuffer();
        buffer.Add("flag-z", "BOOLEAN", false, "svc", "prod");
        buffer.Drain(); // clears pending but NOT seen

        buffer.Add("flag-z", "BOOLEAN", true, "svc", "prod"); // same id, should be deduped

        Assert.Equal(0, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_PendingCount_ReflectsCurrentState()
    {
        var buffer = new FlagRegistrationBuffer();
        Assert.Equal(0, buffer.PendingCount);

        buffer.Add("a", "BOOLEAN", false, null, null);
        Assert.Equal(1, buffer.PendingCount);

        buffer.Add("b", "STRING", "x", null, null);
        Assert.Equal(2, buffer.PendingCount);

        buffer.Drain();
        Assert.Equal(0, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_ThreadSafety_ConcurrentAdds_NoDuplicates()
    {
        var buffer = new FlagRegistrationBuffer();
        var threads = new List<Thread>();

        // 10 threads each trying to add the same 5 ids
        for (int t = 0; t < 10; t++)
        {
            var thread = new Thread(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    buffer.Add($"flag-{i}", "BOOLEAN", false, null, null);
                }
            });
            threads.Add(thread);
        }

        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        // Only 5 distinct ids should be in pending
        Assert.Equal(5, buffer.PendingCount);
    }

    [Fact]
    public void Buffer_Entry_IncludesAllFields()
    {
        var buffer = new FlagRegistrationBuffer();
        buffer.Add("my-flag", "STRING", "default-val", "my-service", "production");

        var batch = buffer.Drain();

        Assert.Single(batch);
        var entry = batch[0];
        Assert.Equal("my-flag", entry.Id);
        Assert.Equal("STRING", entry.Type);
        Assert.Equal("default-val", entry.DefaultValue);
        Assert.Equal("my-service", entry.Service);
        Assert.Equal("production", entry.Environment);
    }

    [Fact]
    public void Buffer_Entry_NullServiceAndEnvironment_Allowed()
    {
        var buffer = new FlagRegistrationBuffer();
        buffer.Add("my-flag", "BOOLEAN", false, null, null);

        var batch = buffer.Drain();
        Assert.Single(batch);
        Assert.Null(batch[0].Service);
        Assert.Null(batch[0].Environment);
    }

    // ---------------------------------------------------------------
    // Typed flag methods — populate buffer with correct type/default
    // ---------------------------------------------------------------

    [Fact]
    public void BooleanFlag_AddsToBuffer_WithCorrectType()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        client.Flags.BooleanFlag("bool-flag", true);

        var buffer = GetFlagBuffer(client.Flags);
        Assert.Equal(1, buffer.PendingCount);

        var batch = buffer.Drain();
        Assert.Equal("bool-flag", batch[0].Id);
        Assert.Equal("BOOLEAN", batch[0].Type);
        Assert.Equal(true, batch[0].DefaultValue);
    }

    [Fact]
    public void StringFlag_AddsToBuffer_WithCorrectType()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        client.Flags.StringFlag("str-flag", "hello");

        var buffer = GetFlagBuffer(client.Flags);
        var batch = buffer.Drain();
        Assert.Single(batch);
        Assert.Equal("str-flag", batch[0].Id);
        Assert.Equal("STRING", batch[0].Type);
        Assert.Equal("hello", batch[0].DefaultValue);
    }

    [Fact]
    public void NumberFlag_AddsToBuffer_WithCorrectType()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        client.Flags.NumberFlag("num-flag", 42.5);

        var buffer = GetFlagBuffer(client.Flags);
        var batch = buffer.Drain();
        Assert.Single(batch);
        Assert.Equal("num-flag", batch[0].Id);
        Assert.Equal("NUMERIC", batch[0].Type);
        Assert.Equal(42.5, batch[0].DefaultValue);
    }

    [Fact]
    public void JsonFlag_AddsToBuffer_WithCorrectType()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        var defaultVal = new Dictionary<string, object?> { ["theme"] = "dark" };
        client.Flags.JsonFlag("json-flag", defaultVal);

        var buffer = GetFlagBuffer(client.Flags);
        var batch = buffer.Drain();
        Assert.Single(batch);
        Assert.Equal("json-flag", batch[0].Id);
        Assert.Equal("JSON", batch[0].Type);
        Assert.Same(defaultVal, batch[0].DefaultValue);
    }

    [Fact]
    public void TypedFlag_AddsServiceAndEnvironment_FromParent()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production", Service = "my-service" },
            httpClient);

        client.Flags.BooleanFlag("flag-with-ctx", false);

        var buffer = GetFlagBuffer(client.Flags);
        var batch = buffer.Drain();
        Assert.Single(batch);
        Assert.Equal("my-service", batch[0].Service);
        Assert.Equal("production", batch[0].Environment);
    }

    [Fact]
    public void BooleanFlag_DeclaredTwice_OnlyOneInBuffer()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        client.Flags.BooleanFlag("dup-flag", false);
        client.Flags.BooleanFlag("dup-flag", true); // same id

        var buffer = GetFlagBuffer(client.Flags);
        Assert.Equal(1, buffer.PendingCount);
    }

    // ---------------------------------------------------------------
    // FlushFlagsAsync — HTTP behavior
    // ---------------------------------------------------------------

    [Fact]
    public async Task FlushFlagsAsync_EmptyBuffer_DoesNotCallApi()
    {
        var (client, handler) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        await client.Flags.FlushFlagsAsync();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FlushFlagsAsync_WithPendingFlags_PostsToBulkEndpoint()
    {
        var requests = new List<HttpRequestMessage>();
        var (client, _) = CreateClient(req =>
        {
            requests.Add(req);
            return Task.FromResult(JsonResponse(BulkResponseJson()));
        });

        client.Flags.BooleanFlag("my-flag", false);

        // Drain the buffer so we can call FlushFlagsAsync manually with something in it
        // We need to re-add to the buffer via a second declaration (different id)
        client.Flags.StringFlag("another-flag", "hello");

        // Drain the auto-added ones and re-add manually via a fresh client approach
        // Instead: directly test via EnsureInitialized which calls FlushFlagsAsync
        requests.Clear();
        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("manual-flag", "BOOLEAN", false, "svc", "env");

        await client.Flags.FlushFlagsAsync();

        Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Contains("/api/v1/flags/bulk", requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task FlushFlagsAsync_SendsCorrectPayload()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(BulkResponseJson());
        });

        // Manually add to the buffer
        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("test-flag", "STRING", "default", "svc", "staging");

        await client.Flags.FlushFlagsAsync();

        Assert.NotNull(capturedBody);
        var doc = JsonDocument.Parse(capturedBody);
        var flags = doc.RootElement.GetProperty("flags");
        Assert.Equal(1, flags.GetArrayLength());
        var flag = flags[0];
        Assert.Equal("test-flag", flag.GetProperty("id").GetString());
        Assert.Equal("STRING", flag.GetProperty("type").GetString());
        Assert.Equal("svc", flag.GetProperty("service").GetString());
        Assert.Equal("staging", flag.GetProperty("environment").GetString());
    }

    [Fact]
    public async Task FlushFlagsAsync_ApiFailure_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("fail-flag", "BOOLEAN", false, null, null);

        // Should not throw
        await client.Flags.FlushFlagsAsync();
    }

    [Fact]
    public async Task FlushFlagsAsync_NetworkError_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ => throw new HttpRequestException("network error"));

        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("net-fail-flag", "BOOLEAN", false, null, null);

        // Should not throw
        await client.Flags.FlushFlagsAsync();
    }

    [Fact]
    public async Task FlushFlagsAsync_DrainsPendingAfterSend()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(BulkResponseJson())));

        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("flag-to-drain", "BOOLEAN", false, null, null);

        await client.Flags.FlushFlagsAsync();

        Assert.Equal(0, buffer.PendingCount);
    }

    // ---------------------------------------------------------------
    // EnsureInitialized — flush before fetch ordering
    // ---------------------------------------------------------------

    [Fact]
    public void EnsureInitialized_FlushesBeforeFetch()
    {
        var requestUrls = new List<string>();
        var (client, _) = CreateClient(req =>
        {
            requestUrls.Add(req.RequestUri!.AbsolutePath);
            // Return appropriate responses based on endpoint
            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath.Contains("bulk"))
                return Task.FromResult(JsonResponse(BulkResponseJson()));
            return Task.FromResult(JsonResponse(EmptyFlagListJson()));
        });

        client.Flags.BooleanFlag("init-flag", false);
        // Drain the buffer so we can re-add and track the flush
        var buffer = GetFlagBuffer(client.Flags);
        buffer.Drain();
        buffer.Add("init-flag", "BOOLEAN", false, null, null); // re-add for the flush

        // Trigger EnsureInitialized by evaluating
        var handle = client.Flags.BooleanFlag("another-init-flag", true);
        // The buffer already drained above; BooleanFlag adds new ones
        // We just need to trigger init and see the bulk POST comes first

        // Reset tracking and trigger init fresh via a new client
        requestUrls.Clear();

        var handler2 = new MockHttpMessageHandler(req =>
        {
            requestUrls.Add(req.RequestUri!.AbsolutePath);
            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath.Contains("bulk"))
                return Task.FromResult(JsonResponse(BulkResponseJson()));
            return Task.FromResult(JsonResponse(EmptyFlagListJson()));
        });
        var httpClient2 = new HttpClient(handler2);
        var client2 = new SmplClient(TestData.DefaultOptions(), httpClient2);

        var handle2 = client2.Flags.BooleanFlag("ordered-flag", false);
        handle2.Get(); // triggers EnsureInitialized

        // First request should be the bulk POST, second should be the list GET
        Assert.True(requestUrls.Count >= 2);
        var bulkIndex = requestUrls.FindIndex(u => u.Contains("bulk"));
        var listIndex = requestUrls.FindIndex(u => u.Contains("/api/v1/flags") && !u.Contains("bulk"));
        Assert.True(bulkIndex < listIndex, $"Expected bulk ({bulkIndex}) before list ({listIndex})");
    }

    // ---------------------------------------------------------------
    // Threshold at 50 triggers Task.Run flush
    // ---------------------------------------------------------------

    [Fact]
    public void TypedFlag_At50Items_TriggersThresholdFlush()
    {
        var requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(BulkResponseJson()));
        });

        // Declare 49 flags — should not trigger threshold flush
        for (int i = 0; i < 49; i++)
            client.Flags.BooleanFlag($"flag-{i}", false);

        var beforeCount = requestCount;

        // 50th flag triggers threshold flush via Task.Run
        client.Flags.BooleanFlag("flag-49", false);

        // Wait up to 5 seconds for the background Task.Run to fire and complete
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref requestCount) <= beforeCount && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        // At least one bulk request should have been made
        Assert.True(requestCount > beforeCount,
            $"Expected threshold flush to trigger a request, but requestCount={requestCount}, beforeCount={beforeCount}");
    }

    // ---------------------------------------------------------------
    // Timer — started after init, disposed on Close
    // ---------------------------------------------------------------

    [Fact]
    public void EnsureInitialized_StartsFlushTimer()
    {
        var (client, _) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("bulk"))
                return Task.FromResult(JsonResponse(BulkResponseJson()));
            return Task.FromResult(JsonResponse(EmptyFlagListJson()));
        });

        var handle = client.Flags.BooleanFlag("timer-test-flag", false);
        handle.Get(); // triggers EnsureInitialized

        // Verify timer was started by checking via reflection
        var timerField = typeof(FlagsClient).GetField("_flagFlushTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timer = timerField!.GetValue(client.Flags);
        Assert.NotNull(timer);
    }

    [Fact]
    public void Close_DisposesTimer()
    {
        var (client, _) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("bulk"))
                return Task.FromResult(JsonResponse(BulkResponseJson()));
            return Task.FromResult(JsonResponse(EmptyFlagListJson()));
        });

        // Initialize to start the timer
        var handle = client.Flags.BooleanFlag("close-test-flag", false);
        handle.Get();

        // Close should dispose the timer without throwing
        client.Flags.Close();

        // Verify timer is null after Close
        var timerField = typeof(FlagsClient).GetField("_flagFlushTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timer = timerField!.GetValue(client.Flags);
        Assert.Null(timer);
    }

    [Fact]
    public void Close_BeforeInit_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        // Close without ever initializing
        client.Flags.Close();
    }

    [Fact]
    public void Close_CalledTwice_DoesNotThrow()
    {
        var (client, _) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("bulk"))
                return Task.FromResult(JsonResponse(BulkResponseJson()));
            return Task.FromResult(JsonResponse(EmptyFlagListJson()));
        });

        var handle = client.Flags.BooleanFlag("double-close-flag", false);
        handle.Get();

        client.Flags.Close();
        client.Flags.Close(); // second call should not throw
    }

    // ---------------------------------------------------------------
    // SmplClient.Dispose — calls Flags.Close()
    // ---------------------------------------------------------------

    [Fact]
    public void SmplClientDispose_CallsFlagsClose()
    {
        var (client, _) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath.Contains("bulk"))
                return Task.FromResult(JsonResponse(BulkResponseJson()));
            return Task.FromResult(JsonResponse(EmptyFlagListJson()));
        });

        // Initialize flags to start the timer
        var handle = client.Flags.BooleanFlag("dispose-test-flag", false);
        handle.Get();

        // Dispose should not throw and should clean up flags resources
        client.Dispose();

        // After dispose, the timer should be null
        var timerField = typeof(FlagsClient).GetField("_flagFlushTimer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var timer = timerField!.GetValue(client.Flags);
        Assert.Null(timer);
    }

    [Fact]
    public void SmplClientDispose_WithoutInit_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(EmptyFlagListJson())));

        // Dispose without initializing flags
        client.Dispose();
    }

    // ---------------------------------------------------------------
    // Threshold flush — StringFlag, NumberFlag, JsonFlag
    // ---------------------------------------------------------------

    [Fact]
    public void StringFlag_At50Items_TriggersThresholdFlush()
    {
        var requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(BulkResponseJson()));
        });

        // Each unique string flag id adds to the buffer
        for (int i = 0; i < 49; i++)
            client.Flags.StringFlag($"str-flag-{i}", "val");
        var beforeCount = requestCount;

        client.Flags.StringFlag("str-flag-49", "val"); // 50th triggers threshold

        Thread.Sleep(300);
        Assert.True(requestCount > beforeCount,
            $"Expected string flag threshold flush, requestCount={requestCount}, beforeCount={beforeCount}");
    }

    [Fact]
    public void NumberFlag_At50Items_TriggersThresholdFlush()
    {
        var requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(BulkResponseJson()));
        });

        for (int i = 0; i < 49; i++)
            client.Flags.NumberFlag($"num-flag-{i}", i);
        var beforeCount = requestCount;

        client.Flags.NumberFlag("num-flag-49", 49); // 50th triggers threshold

        Thread.Sleep(300);
        Assert.True(requestCount > beforeCount,
            $"Expected number flag threshold flush, requestCount={requestCount}, beforeCount={beforeCount}");
    }

    [Fact]
    public void JsonFlag_At50Items_TriggersThresholdFlush()
    {
        var requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(BulkResponseJson()));
        });

        var defaultVal = new Dictionary<string, object?> { ["x"] = 1 };
        for (int i = 0; i < 49; i++)
            client.Flags.JsonFlag($"json-flag-{i}", defaultVal);
        var beforeCount = requestCount;

        client.Flags.JsonFlag("json-flag-49", defaultVal); // 50th triggers threshold

        Thread.Sleep(300);
        Assert.True(requestCount > beforeCount,
            $"Expected json flag threshold flush, requestCount={requestCount}, beforeCount={beforeCount}");
    }

    // ---------------------------------------------------------------
    // Timer callback — FlushTimerCallback
    // ---------------------------------------------------------------

    [Fact]
    public void FlushTimerCallback_WithPendingFlags_SendsBulkRequest()
    {
        var requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(BulkResponseJson()));
        });

        // Add something to the buffer
        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("timer-callback-flag", "BOOLEAN", false, null, null);

        // Call the timer callback directly
        client.Flags.FlushTimerCallback();

        Assert.Equal(1, requestCount);
    }

    [Fact]
    public void FlushTimerCallback_ApiFailure_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("timer-fail-flag", "BOOLEAN", false, null, null);

        // Should not throw even when flush fails
        client.Flags.FlushTimerCallback();
    }

    [Fact]
    public void FlushTimerCallback_NetworkError_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ => throw new HttpRequestException("network error"));

        var buffer = GetFlagBuffer(client.Flags);
        buffer.Add("timer-net-fail-flag", "BOOLEAN", false, null, null);

        // Should not throw
        client.Flags.FlushTimerCallback();
    }

    // ---------------------------------------------------------------
    // Helper: access internal FlagRegistrationBuffer via reflection
    // ---------------------------------------------------------------

    private static FlagRegistrationBuffer GetFlagBuffer(FlagsClient flagsClient)
    {
        var field = typeof(FlagsClient).GetField("_flagBuffer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return (FlagRegistrationBuffer)field!.GetValue(flagsClient)!;
    }
}
