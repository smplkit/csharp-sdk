using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Internal;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Tests for the runtime <see cref="FlagsClient"/>: handle declaration,
/// listeners, refresh/refetch, push-driven updates, context provider,
/// flag registration buffer, lifecycle (Close).
/// </summary>
public class FlagsRuntimeTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) MakeClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), http);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    private static string FlagListJson(params (string Id, string Type, string Default, bool? StagingEnabled, object? StagingDefault)[] flags)
    {
        var entries = flags.Select(f =>
        {
            var stagingEnabled = f.StagingEnabled?.ToString().ToLowerInvariant() ?? "true";
            var stagingDefault = f.StagingDefault is null ? "null"
                : f.StagingDefault is string s ? $"\"{s}\""
                : f.StagingDefault.ToString();
            return $$"""
            {
                "id": "{{f.Id}}",
                "type": "flag",
                "attributes": {
                    "id": "{{f.Id}}",
                    "name": "{{f.Id}}",
                    "type": "{{f.Type}}",
                    "default": {{f.Default}},
                    "values": [],
                    "description": null,
                    "environments": {
                        "test": {"enabled": {{stagingEnabled}}, "default": {{stagingDefault}}, "rules": []}
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
            """;
        });
        return $$"""
            { "data": [ {{string.Join(",", entries)}} ] }
            """;
    }

    [Fact]
    public void StringFlag_Declaration_DefaultsAreStored()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var handle = client.Flags.StringFlag("greeting", "hi");
        Assert.Equal("greeting", handle.Id);
        Assert.Equal("hi", handle.Default);
    }

    [Fact]
    public void NumberFlag_Declaration_StoresHandle()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var handle = client.Flags.NumberFlag("retries", 3.0);
        Assert.Equal("retries", handle.Id);
        Assert.Equal(3.0, handle.Default);
    }

    [Fact]
    public void JsonFlag_Declaration_StoresHandle()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var handle = client.Flags.JsonFlag("settings", new Dictionary<string, object?> { ["x"] = 1 });
        Assert.Equal("settings", handle.Id);
    }

    [Fact]
    public void StringFlag_Get_NoStore_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var handle = client.Flags.StringFlag("not-in-server", "fallback");
        Assert.Equal("fallback", handle.Get());
    }

    [Fact]
    public void NumberFlag_Get_ReturnsServerValue()
    {
        var body = FlagListJson(("retries", "NUMERIC", "5", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));
        var handle = client.Flags.NumberFlag("retries", 3);
        Assert.Equal(5, handle.Get());
    }

    [Fact]
    public void JsonFlag_Get_NotInStore_ReturnsCodeDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var defaultVal = new Dictionary<string, object?> { ["k"] = "v" };
        var handle = client.Flags.JsonFlag("missing-flag", defaultVal);
        var result = handle.Get();
        Assert.Equal("v", result["k"]);
    }

    [Fact]
    public async Task OnChange_GlobalListener_FiresOnRefresh()
    {
        var body = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));

        var fired = new List<FlagChangeEvent>();
        client.Flags.OnChange(evt => fired.Add(evt));

        // Initialize
        client.Flags.BooleanFlag("f1", false).Get();
        // Refresh fires listeners for all known flags
        await client.Flags.RefreshAsync();

        Assert.NotEmpty(fired);
    }

    [Fact]
    public async Task RefreshAsync_ClearsCacheAndFiresListeners()
    {
        var body = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));

        var fired = false;
        client.Flags.OnChange(_ => fired = true);

        // Init
        client.Flags.BooleanFlag("f1", false).Get();
        await client.Flags.RefreshAsync();

        Assert.True(fired);
    }

    [Fact]
    public void OnChange_ScopedListener_FiresOnlyForMatchingId()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(FlagListJson(("f1", "BOOLEAN", "true", true, null)))));

        var f1Fired = 0;
        var f2Fired = 0;
        client.Flags.OnChange("f1", _ => f1Fired++);
        client.Flags.OnChange("f2", _ => f2Fired++);

        // Drive a fire via reflection of FireChangeListeners
        var method = typeof(FlagsClient).GetMethod("FireChangeListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[] { "f1", "test", false });

        Assert.Equal(1, f1Fired);
        Assert.Equal(0, f2Fired);
    }

    [Fact]
    public void OnChange_ListenerThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));

        client.Flags.OnChange(_ => throw new InvalidOperationException("bad listener"));
        bool secondFired = false;
        client.Flags.OnChange(_ => secondFired = true);

        var method = typeof(FlagsClient).GetMethod("FireChangeListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[] { "any", "test", false });

        Assert.True(secondFired);
    }

    [Fact]
    public void ConnectionStatus_NotInitialized_Disconnected()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        Assert.Equal("disconnected", client.Flags.ConnectionStatus);
    }

    [Fact]
    public void Stats_StartsAtZero()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var stats = client.Flags.Stats;
        Assert.Equal(0, stats.CacheHits);
        Assert.Equal(0, stats.CacheMisses);
    }

    [Fact]
    public void EvaluateHandle_CacheHitsTracked()
    {
        var body = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));

        var handle = client.Flags.BooleanFlag("f1", false);
        handle.Get();
        var first = client.Flags.Stats;
        handle.Get(); // second call should hit cache
        var second = client.Flags.Stats;

        Assert.True(second.CacheHits > first.CacheHits);
    }

    [Fact]
    public void HandleFlagDeleted_RemovesFromStoreAndFires()
    {
        var body = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));
        client.Flags.BooleanFlag("f1", false).Get(); // init

        var fired = false;
        client.Flags.OnChange("f1", evt => fired = evt.Deleted);

        var method = typeof(FlagsClient).GetMethod("HandleFlagDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "f1" },
        });

        Assert.True(fired);
    }

    [Fact]
    public void HandleFlagDeleted_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        client.Flags.BooleanFlag("f1", false).Get(); // init

        var method = typeof(FlagsClient).GetMethod("HandleFlagDeleted",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // No exception even with empty data
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public void HandleFlagChanged_FetchesAndFires()
    {
        var listBody = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var singleBody = """
            {
                "data": {
                    "id": "f1",
                    "type": "flag",
                    "attributes": {
                        "id": "f1",
                        "name": "F1",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [],
                        "description": "updated",
                        "environments": {"test": {"enabled": true, "default": false, "rules": []}}
                    }
                }
            }
            """;
        var (client, _) = MakeClient(req =>
        {
            // Distinguish list (no id in URL) vs single get (has /flags/<id>)
            if (req.RequestUri!.AbsolutePath.EndsWith("/flags"))
                return Task.FromResult(Json(listBody));
            return Task.FromResult(Json(singleBody));
        });
        client.Flags.BooleanFlag("f1", true).Get(); // initial value true

        var fired = false;
        client.Flags.OnChange("f1", _ => fired = true);

        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "f1" },
        });

        Assert.True(fired);
    }

    [Fact]
    public void HandleFlagChanged_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        client.Flags.BooleanFlag("f1", false).Get();
        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // No exception
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public void HandleFlagsChanged_FullRefetch()
    {
        var listV1 = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var listV2 = FlagListJson(
            ("f1", "BOOLEAN", "false", true, null),
            ("f2", "STRING", "\"x\"", true, null));
        int listCall = 0;
        var (client, _) = MakeClient(req =>
        {
            // Distinguish list (GET on /flags) from bulk-register (POST on /bulk_register)
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                listCall++;
                return Task.FromResult(Json(listCall <= 1 ? listV1 : listV2));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Flags.BooleanFlag("f1", false).Get(); // init (call 1: list)

        var fired = new List<string>();
        client.Flags.OnChange(evt => fired.Add(evt.Id));

        var method = typeof(FlagsClient).GetMethod("HandleFlagsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?>(),
        });

        Assert.NotEmpty(fired);
    }

    [Fact]
    public void HandleFlagsChanged_Unchanged_NoFire()
    {
        var body = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));
        client.Flags.BooleanFlag("f1", false).Get();

        var fired = false;
        client.Flags.OnChange(_ => fired = true);

        var method = typeof(FlagsClient).GetMethod("HandleFlagsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?>(),
        });

        Assert.False(fired);
    }

    [Fact]
    public void HandleFlagsChanged_ServerError_Swallowed()
    {
        int listCall = 0;
        var (client, _) = MakeClient(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                listCall++;
                if (listCall <= 1)
                    return Task.FromResult(Json(FlagListJson(("f1", "BOOLEAN", "true", true, null))));
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(Json("{}"));
        });
        client.Flags.BooleanFlag("f1", false).Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagsChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // No exception
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public void HandleReconnectRefetch_ReusesFlagsChangedBulkPath()
    {
        var listV1 = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var listV2 = FlagListJson(("f1", "BOOLEAN", "false", true, null));
        int listCall = 0;
        var (client, _) = MakeClient(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                listCall++;
                return Task.FromResult(Json(listCall <= 1 ? listV1 : listV2));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Flags.BooleanFlag("f1", false).Get(); // init (call 1: list)

        var fired = new List<FlagChangeEvent>();
        client.Flags.OnChange(evt => fired.Add(evt));

        var method = typeof(FlagsClient).GetMethod("HandleReconnectRefetch",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, Array.Empty<object>());

        // The refetch reuses the flags_changed bulk path: listeners fire for
        // changed keys only, with the push source label.
        Assert.NotEmpty(fired);
        Assert.All(fired, e => Assert.Equal("push", e.Source));
    }

    [Fact]
    public async Task EventStreamReconnect_RefetchesFlags_AndFiresPushListeners()
    {
        // Full-stack staleness regression: the flags module registers its bulk
        // refetch with the shared event stream; dropping and re-establishing
        // the stream must re-fetch all flags and fire change listeners
        // (source "push") for values that moved while disconnected.
        var listV1 = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var listV2 = FlagListJson(("f1", "BOOLEAN", "false", true, null));
        int listCall = 0;
        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
            {
                var n = Interlocked.Increment(ref listCall);
                return Task.FromResult(Json(n <= 1 ? listV1 : listV2));
            }
            return Task.FromResult(Json("{}"));
        });
        var http = new HttpClient(handler);
        var factory = new GeneratedClientFactory(http, new SmplClientOptions
        {
            ApiKey = TestData.ApiKey,
            BaseDomain = "example.test",
        });

        var streams = new List<SsePushStream>();
        var server = new SseTestServer(_ =>
        {
            var s = new SsePushStream();
            lock (streams) streams.Add(s);
            return SseTestServer.CreateSseResponse(s);
        });
        EventStream? es = null;
        EventStream EnsureEvents()
        {
            if (es is null)
            {
                es = new EventStream(TestData.ApiKey, server.SendAsync);
                es.DelayAsync = (_, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                };
                es.Start();
            }
            return es;
        }

        var flags = new FlagsClient(factory, TestData.ApiKey, EnsureEvents,
            new ContextRegistrationBuffer(lruSize: 100, flushSize: 100));
        try
        {
            var handle = flags.BooleanFlag("f1", false);
            Assert.True(handle.Get()); // v1 default served

            var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            flags.OnChange(evt =>
            {
                if (evt.Source == "push") pushed.TrySetResult();
            });

            await es!.WaitForInitialConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            // Drop the stream: the reconnect must trigger the registered refetch.
            lock (streams) streams[0].Complete();

            await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(handle.Get()); // v2 default served after the refetch
        }
        finally
        {
            flags.Dispose();
            if (es is not null) await es.StopAsync();
        }
    }

    [Fact]
    public void HandleFlagChanged_ServerError_Swallowed()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.EndsWith("/flags"))
                return Task.FromResult(Json(FlagListJson(("f1", "BOOLEAN", "true", true, null))));
            if (req.RequestUri!.AbsolutePath.Contains("/flags/f1"))
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            return Task.FromResult(Json("{}"));
        });
        client.Flags.BooleanFlag("f1", false).Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // No exception
        method.Invoke(client.Flags, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "f1" },
        });
    }

    [Fact]
    public void FlushTimerCallback_NoPending_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        client.Flags.BooleanFlag("f1", false).Get();
        var method = typeof(FlagsClient).GetMethod("FlushTimerCallback",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // No exception even when buffer empty
        method.Invoke(client.Flags, Array.Empty<object>());
    }

    [Fact]
    public void EvaluateHandle_WithExplicitContext()
    {
        var body = FlagListJson(("f1", "BOOLEAN", "true", true, null));
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));
        var ctx = new[] { new Smplkit.Context("user", "u-1") };
        var handle = client.Flags.BooleanFlag("f1", false);
        var result = handle.Get(context: ctx);
        Assert.True(result);
    }

    [Fact]
    public void Close_Idempotent()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        // Trigger init
        client.Flags.BooleanFlag("f1", false).Get();
        var method = typeof(FlagsClient).GetMethod("Close",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, Array.Empty<object>());
        // Second call should not throw
        method.Invoke(client.Flags, Array.Empty<object>());
    }

    [Fact]
    public void FlushFlagsAsync_ServerError_Swallowed()
    {
        int call = 0;
        var (client, _) = MakeClient(req =>
        {
            call++;
            // First call (initial list) succeeds; subsequent (bulk register) fail
            if (req.RequestUri!.AbsolutePath.Contains("/bulk"))
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            return Task.FromResult(Json("""{"data":[]}"""));
        });
        // Declaration adds to buffer; init flushes it
        client.Flags.BooleanFlag("f1", false).Get();
        // No throw
    }
}
