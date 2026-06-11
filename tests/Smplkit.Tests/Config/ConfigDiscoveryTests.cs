using System.Net;
using System.Reflection;
using System.Text;
using Smplkit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the declarative discovery API: the registration buffer, the
/// bulk-upload wiring on <see cref="ConfigsClient"/>, and the runtime
/// <see cref="ConfigClient.Bind{T}(string, T, object?)"/> /
/// <see cref="ConfigClient.GetValue(string, string)"/> /
/// <see cref="ConfigClient.GetValueOr{T}(string, string, T)"/> surface.
/// </summary>
public class ConfigDiscoveryTests
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

    private static HttpResponseMessage ServerError()
        => Json("""{"errors":[{"detail":"boom"}]}""", HttpStatusCode.InternalServerError);

    private static bool IsConfigsBulkPost(HttpRequestMessage req)
        => req.Method == HttpMethod.Post
        && req.RequestUri!.AbsolutePath.EndsWith("/configs/bulk");

    private static bool IsConfigsList(HttpRequestMessage req)
        => req.Method == HttpMethod.Get
        && req.RequestUri!.AbsolutePath.Contains("/configs")
        && !req.RequestUri.AbsolutePath.Contains("/configs/bulk");

    private const string EmptyListJson = """{"data":[]}""";

    private const string ConfigListJson = """
        {
            "data": [
                {
                    "id": "billing",
                    "type": "config",
                    "attributes": {
                        "id": "billing",
                        "name": "Billing",
                        "description": null,
                        "parent": null,
                        "items": {
                            "max_seats": {"value": 25, "type": "NUMBER"},
                            "tier": {"value": "pro", "type": "STRING"},
                            "enabled": {"value": true, "type": "BOOLEAN"},
                            "ratio": {"value": 1.5, "type": "NUMBER"}
                        },
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    // ==================================================================
    // 1. ConfigRegistrationBuffer
    // ==================================================================

    [Fact]
    public void Buffer_Declare_RecordsMetaAndPending()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc", "prod", parent: null, name: "Billing", description: "Plan limits");
        Assert.Equal(1, buf.PendingCount);
    }

    [Fact]
    public void Buffer_Declare_FirstWriterWins()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc-a", "prod", null, "Billing-A", "first");
        buf.Declare("billing", "svc-b", "stg", null, "Billing-B", "second");
        var batch = buf.Drain();
        Assert.Single(batch);
        Assert.Equal("svc-a", batch[0].Service);
        Assert.Equal("prod", batch[0].Environment);
        Assert.Equal("Billing-A", batch[0].Name);
        Assert.Equal("first", batch[0].Description);
    }

    [Fact]
    public void Buffer_AddItem_NoMeta_IsNoOp()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.AddItem("billing", "max_seats", "NUMBER", 5, "seats default");
        Assert.Equal(0, buf.PendingCount);
        Assert.Empty(buf.Drain());
    }

    [Fact]
    public void Buffer_AddItem_AfterDeclare_QueuesItem()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc", "prod", null, "Billing", null);
        buf.AddItem("billing", "max_seats", "NUMBER", 5, "Default seats.");
        var batch = buf.Drain();
        Assert.Single(batch);
        Assert.Single(batch[0].Items);
        var item = batch[0].Items["max_seats"];
        Assert.Equal(5, item.DefaultValue);
        Assert.Equal("NUMBER", item.ItemType);
        Assert.Equal("Default seats.", item.Description);
    }

    [Fact]
    public void Buffer_AddItem_SecondTimeSameKey_IsNoOp()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc", "prod", null, null, null);
        buf.AddItem("billing", "max_seats", "NUMBER", 5, null);
        buf.AddItem("billing", "max_seats", "NUMBER", 99, "different default");
        var batch = buf.Drain();
        Assert.Single(batch[0].Items);
        Assert.Equal(5, batch[0].Items["max_seats"].DefaultValue);
    }

    [Fact]
    public void Buffer_Drain_ClearsPending()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("a", "svc", "env", null, null, null);
        Assert.Equal(1, buf.PendingCount);
        buf.Drain();
        Assert.Equal(0, buf.PendingCount);
    }

    [Fact]
    public void Buffer_Drain_EmptyReturnsEmpty()
    {
        var buf = new ConfigRegistrationBuffer();
        Assert.Empty(buf.Drain());
    }

    [Fact]
    public void Buffer_PostDrain_ItemAlreadySent_NotResentEvenAfterRequeue()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc", "prod", null, null, null);
        buf.AddItem("billing", "max_seats", "NUMBER", 5, null);
        var batch1 = buf.Drain();
        Assert.Single(batch1[0].Items);

        buf.AddItem("billing", "max_seats", "NUMBER", 5, null);
        Assert.Equal(0, buf.PendingCount);
        Assert.Empty(buf.Drain());
    }

    [Fact]
    public void Buffer_PostDrain_NewItemAttributesToSameConfig()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc", "prod", null, "Billing", null);
        buf.AddItem("billing", "first", "STRING", "a", null);
        buf.Drain();

        buf.AddItem("billing", "second", "NUMBER", 7, "added later");
        var batch = buf.Drain();
        Assert.Single(batch);
        Assert.Equal("billing", batch[0].Id);
        Assert.Equal("Billing", batch[0].Name);
        Assert.Single(batch[0].Items);
        Assert.Equal("second", batch[0].Items.Keys.First());
    }

    [Fact]
    public void Buffer_Declare_WithParent_RetainsParent()
    {
        var buf = new ConfigRegistrationBuffer();
        buf.Declare("billing", "svc", "prod", parent: "common", name: null, description: null);
        var batch = buf.Drain();
        Assert.Equal("common", batch[0].Parent);
    }

    // ==================================================================
    // 2. ConfigsClient.Register* / FlushAsync
    // ==================================================================

    [Fact]
    public async Task ConfigsClient_FlushAsync_NoPending_DoesNothing()
    {
        bool bulkCalled = false;
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) bulkCalled = true;
            return Task.FromResult(Json(EmptyListJson));
        });

        await client.Config.FlushAsync();
        Assert.False(bulkCalled);
    }

    [Fact]
    public async Task ConfigsClient_RegisterConfig_QueuesDeclaration_FlushPosts()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.RegisterConfig(
            "billing", service: "svc", environment: "prod",
            parent: "common", name: "Billing", description: "Plan limits");
        Assert.Equal(1, client.Config.PendingCount);

        await client.Config.FlushAsync();
        Assert.Equal(0, client.Config.PendingCount);
        Assert.NotNull(lastBody);
        Assert.Contains("\"billing\"", lastBody);
        Assert.Contains("\"common\"", lastBody);
        Assert.Contains("\"Billing\"", lastBody);
        Assert.Contains("\"Plan limits\"", lastBody);
    }

    [Fact]
    public async Task ConfigsClient_RegisterConfigItem_FlushIncludesAllTypes()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.RegisterConfig("billing", "svc", "prod");
        client.Config.RegisterConfigItem("billing", "max_seats", "NUMBER", 5, "seats");
        client.Config.RegisterConfigItem("billing", "tier", "STRING", "free", null);
        client.Config.RegisterConfigItem("billing", "enabled", "BOOLEAN", false, null);
        client.Config.RegisterConfigItem("billing", "payload", "JSON", null, null);
        client.Config.RegisterConfigItem("billing", "weird", "WEIRD", "?", null);

        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("STRING", lastBody);
        Assert.Contains("NUMBER", lastBody);
        Assert.Contains("BOOLEAN", lastBody);
        Assert.Contains("JSON", lastBody);
        Assert.Contains("\"seats\"", lastBody);
    }

    [Fact]
    public async Task ConfigsClient_FlushAsync_ServerError_Propagates()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) return Task.FromResult(ServerError());
            return Task.FromResult(Json(EmptyListJson));
        });

        client.Config.RegisterConfig("billing", "svc", "prod");
        // FlushAsync propagates failures to the caller; the discovery-flush call
        // sites (threshold flush, EnsureConnected) swallow them so they never
        // reach customer code. Drained entries are not requeued.
        await Assert.ThrowsAsync<Smplkit.Errors.SmplkitException>(() => client.Config.FlushAsync());
        Assert.Equal(0, client.Config.PendingCount);
    }

    [Fact]
    public async Task ConfigsClient_RegisterConfig_ThresholdTriggersBackgroundFlush()
    {
        int bulkCount = 0;
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) { Interlocked.Increment(ref bulkCount); return Task.FromResult(Json(EmptyListJson)); }
            return Task.FromResult(Json(EmptyListJson));
        });

        for (int i = 0; i < 51; i++)
            client.Config.RegisterConfig($"cfg-{i}", "svc", "prod");

        for (int i = 0; i < 20 && bulkCount == 0; i++)
            await Task.Delay(25);
        Assert.True(bulkCount >= 1, $"Expected a bulk POST, got {bulkCount}");
    }

    [Fact]
    public async Task ConfigsClient_RegisterConfigItem_ThresholdTriggersBackgroundFlush()
    {
        int bulkCount = 0;
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) { Interlocked.Increment(ref bulkCount); return Task.FromResult(Json(EmptyListJson)); }
            return Task.FromResult(Json(EmptyListJson));
        });

        for (int i = 0; i < 50; i++)
            client.Config.RegisterConfig($"cfg-{i}", "svc", "prod");
        client.Config.RegisterConfig("cfg-extra", "svc", "prod");
        client.Config.RegisterConfigItem("cfg-extra", "k", "STRING", "v", null);

        for (int i = 0; i < 20 && bulkCount == 0; i++)
            await Task.Delay(25);
        Assert.True(bulkCount >= 1);
    }

    // ==================================================================
    // 3. ConfigClient.Bind — POCO path
    // ==================================================================

    public class Billing
    {
        public int MaxSeats { get; set; } = 5;
        public string Tier { get; set; } = "free";
        public bool Enabled { get; set; } = true;
        public double Ratio { get; set; } = 0.5;
    }

    public class Plan
    {
        public int MaxSeats { get; set; } = 5;
        public int TrialDays { get; set; } = 14;
    }

    public class Nested
    {
        public Plan Plan { get; set; } = new();
        public string Tier { get; set; } = "free";
    }

    [Fact]
    public void Bind_Poco_ReturnsSameInstance()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var billing = new Billing();
        var returned = client.Config.Bind("billing", billing);
        Assert.Same(billing, returned);
    }

    [Fact]
    public void Bind_Poco_Idempotent_ReturnsOriginalIgnoringNewArgument()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var first = client.Config.Bind("billing", new Billing { Tier = "first" });
        var second = client.Config.Bind("billing", new Billing { Tier = "second" });
        Assert.Same(first, second);
        Assert.Equal("first", second.Tier);
    }

    [Fact]
    public void Bind_Poco_NullTarget_Throws()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        Assert.Throws<ArgumentNullException>(() =>
            client.Config.Bind<Billing>("billing", null!));
    }

    [Fact]
    public async Task Bind_Poco_RegistersSnakeCasedKeys()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.Bind("billing", new Billing());
        // Bind buffers the discovery declaration; drain it so the bulk POST fires.
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("\"max_seats\"", lastBody);
        Assert.Contains("\"tier\"", lastBody);
        Assert.Contains("\"enabled\"", lastBody);
        Assert.Contains("\"ratio\"", lastBody);
    }

    [Fact]
    public async Task Bind_Poco_RegistersInferredTypes()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.Bind("billing", new Billing());
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("NUMBER", lastBody);
        Assert.Contains("STRING", lastBody);
        Assert.Contains("BOOLEAN", lastBody);
    }

    [Fact]
    public async Task Bind_Poco_FlattensNestedProperties()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.Bind("billing", new Nested());
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("plan.max_seats", lastBody);
        Assert.Contains("plan.trial_days", lastBody);
        Assert.Contains("\"tier\"", lastBody);
    }

    [Fact]
    public void Bind_Poco_SyncFromCacheOnFirstBind()
    {
        // Server has max_seats=25; the in-code default is 5. After Bind,
        // the bound POCO reflects the server-side value.
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        var billing = client.Config.Bind("billing", new Billing());
        Assert.Equal(25, billing.MaxSeats);
        Assert.Equal("pro", billing.Tier);
        Assert.True(billing.Enabled);
        Assert.Equal(1.5, billing.Ratio);
    }

    [Fact]
    public void Bind_Poco_InPlaceMutationOnWebSocketEvent()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var billing = client.Config.Bind("billing", new Billing());

        // Drive a change via DiffAndFire (the websocket dispatch entry point)
        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["billing"] = new() { ["max_seats"] = 5L },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["billing"] = new() { ["max_seats"] = 42L },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "websocket" });

        Assert.Equal(42, billing.MaxSeats);
    }

    [Fact]
    public void Bind_Poco_NestedMutationOnWebSocketEvent()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var nested = client.Config.Bind("svc", new Nested());

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["svc"] = new() { ["plan.max_seats"] = 5L },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["svc"] = new() { ["plan.max_seats"] = 100L },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "websocket" });

        Assert.Equal(100, nested.Plan.MaxSeats);
    }

    public record BillingRecord
    {
        public int MaxSeats { get; init; } = 5;
        public string Tier { get; init; } = "free";
    }

    [Fact]
    public void Bind_RecordWithInitOnly_MutatedViaBackingField()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var billing = client.Config.Bind("billing", new BillingRecord());

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["billing"] = new() { ["max_seats"] = 5L },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["billing"] = new() { ["max_seats"] = 42L },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "websocket" });

        Assert.Equal(42, billing.MaxSeats);
    }

    public class ReadOnlyProps
    {
        public int Computed => 99;
        public int Plain { get; set; } = 5;
    }

    [Fact]
    public void Bind_Poco_PropertyWithoutBackingField_GracefullySkipped()
    {
        // `Computed` has no backing field — applying a change to it
        // must not throw, even though it can't be set.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var inst = client.Config.Bind("svc", new ReadOnlyProps());

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["svc"] = new() { ["computed"] = 99L },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["svc"] = new() { ["computed"] = 100L },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "websocket" });
        // Computed unchanged; no exception.
        Assert.Equal(99, inst.Computed);
    }

    [Fact]
    public void Bind_Poco_UnknownKeyOnWebSocketEvent_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var billing = client.Config.Bind("billing", new Billing());

        // Server pushes a key the POCO doesn't declare — should not throw.
        var oldCache = new Dictionary<string, Dictionary<string, object?>>();
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["billing"] = new() { ["unrecognized"] = "x" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "websocket" });

        Assert.Equal(5, billing.MaxSeats);
    }

    [Fact]
    public void Bind_Poco_NullIntermediate_NoOp()
    {
        // Nested path where intermediate is null — should not throw on apply.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));

        var instance = new NullablePlan();
        client.Config.Bind("svc", instance);

        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, Dictionary<string, object?>>(),
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["svc"] = new() { ["plan.max_seats"] = 9L },
            },
            "websocket",
        });
        Assert.Null(instance.Plan);
    }

    public class NullablePlan
    {
        public Plan? Plan { get; set; }
    }

    // ==================================================================
    // 4. ConfigClient.Bind — dictionary path
    // ==================================================================

    [Fact]
    public void Bind_Dict_ReturnsSameInstance()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var dict = new Dictionary<string, object?>
        {
            ["pool_size"] = 10,
            ["host"] = "db.example",
        };
        var returned = client.Config.Bind("db", dict);
        Assert.Same(dict, returned);
    }

    [Fact]
    public async Task Bind_Dict_RegistersAllKeys()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.Bind("db", new Dictionary<string, object?>
        {
            ["pool_size"] = 10,
            ["host"] = "db.example",
            ["enabled"] = true,
        });
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("\"pool_size\"", lastBody);
        Assert.Contains("\"host\"", lastBody);
        Assert.Contains("\"enabled\"", lastBody);
        Assert.Contains("NUMBER", lastBody);
        Assert.Contains("STRING", lastBody);
        Assert.Contains("BOOLEAN", lastBody);
    }

    [Fact]
    public async Task Bind_Dict_FlattensNestedDicts()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        client.Config.Bind("db", new Dictionary<string, object?>
        {
            ["primary"] = new Dictionary<string, object?>
            {
                ["host"] = "db.example",
                ["port"] = 5432,
            },
            ["pool_size"] = 10,
        });

        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("primary.host", lastBody);
        Assert.Contains("primary.port", lastBody);
        Assert.Contains("pool_size", lastBody);
    }

    [Fact]
    public void Bind_Dict_InPlaceMutationOnWebSocketEvent()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var dict = client.Config.Bind("db", new Dictionary<string, object?>
        {
            ["pool_size"] = 10,
        });

        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["db"] = new() { ["pool_size"] = 10L },
            },
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["db"] = new() { ["pool_size"] = 50L },
            },
            "websocket",
        });

        Assert.Equal(50L, dict["pool_size"]);
    }

    [Fact]
    public void Bind_Dict_NestedMutation()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var dict = client.Config.Bind("db", new Dictionary<string, object?>
        {
            ["primary"] = new Dictionary<string, object?>
            {
                ["host"] = "db.example",
            },
        });

        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, Dictionary<string, object?>>(),
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["db"] = new() { ["primary.host"] = "db-new" },
            },
            "websocket",
        });

        var nested = (IDictionary<string, object?>)dict["primary"]!;
        Assert.Equal("db-new", nested["host"]);
    }

    [Fact]
    public void Bind_Dict_NestedMutation_MissingIntermediate_NoOp()
    {
        // Dict path with missing intermediate key on apply.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var dict = client.Config.Bind("db", new Dictionary<string, object?>
        {
            ["primary"] = new Dictionary<string, object?> { ["host"] = "x" },
        });

        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, Dictionary<string, object?>>(),
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["db"] = new() { ["unknown.subkey"] = 1L },
            },
            "websocket",
        });
        // Original dict unchanged.
        Assert.False(dict.ContainsKey("unknown"));
    }

    // ==================================================================
    // 5. Parent chaining
    // ==================================================================

    [Fact]
    public async Task Bind_Parent_AcceptsPreviouslyBoundObject()
    {
        // Bind flushes the buffer only as part of the first EnsureInitialized.
        // Subsequent Binds queue their declarations but do not auto-flush;
        // we trigger a manual flush to capture the second POST body.
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        var common = client.Config.Bind("common", new Dictionary<string, object?> { ["k"] = "v" });
        client.Config.Bind("billing", new Billing(), parent: common);
        await client.Config.FlushAsync();

        Assert.NotNull(lastBody);
        Assert.Contains("\"parent\":\"common\"", lastBody);
    }

    [Fact]
    public void Bind_Parent_AcceptsBoundPoco()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var common = client.Config.Bind("common", new Plan());
        var child = client.Config.Bind("billing", new Billing(), parent: common);
        Assert.NotNull(child);
    }

    [Fact]
    public void Bind_Parent_NotPreviouslyBound_Throws()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        Assert.Throws<ArgumentException>(() =>
            client.Config.Bind("billing", new Billing(), parent: new Billing()));
    }

    // ==================================================================
    // 6. Pre-fetch buffer flush
    // ==================================================================

    [Fact]
    public void PreFetchFlush_RunsBeforeInitialList()
    {
        var calls = new List<string>();
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) { calls.Add("bulk"); return Task.FromResult(Json(EmptyListJson)); }
            if (IsConfigsList(req)) { calls.Add("list"); return Task.FromResult(Json(EmptyListJson)); }
            return Task.FromResult(Json("{}"));
        });

        // Buffer a discovery declaration BEFORE the first live use so the
        // connect-time flush has something to send. EnsureConnected drains the
        // buffer before the initial config list, so "bulk" must precede "list".
        var register = typeof(ConfigClient).GetMethod("RegisterConfig",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        register.Invoke(client.Config, new object?[] { "billing", "svc", "test", null, null, null });

        // Any live call triggers EnsureConnected (flush-then-list). The list is
        // empty so Subscribe ultimately raises NotFound — but only after connect
        // has run, which is all this test observes.
        Assert.Throws<NotFoundException>(() => client.Config.Subscribe("billing"));

        Assert.True(calls.Count >= 2);
        Assert.Equal("bulk", calls[0]);
        Assert.Equal("list", calls[1]);
    }

    [Fact]
    public void Bind_PreFetchFlushFails_DoesNotPropagate()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) return Task.FromResult(ServerError());
            return Task.FromResult(Json(EmptyListJson));
        });
        var billing = client.Config.Bind("billing", new Billing());
        Assert.NotNull(billing);
    }

    // ==================================================================
    // 7. GetValue / GetValueOr
    // ==================================================================

    [Fact]
    public void GetValue_PresentKey_ReturnsValue()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        Assert.Equal(25L, client.Config.GetValue("billing", "max_seats"));
        Assert.Equal("pro", client.Config.GetValue("billing", "tier"));
    }

    [Fact]
    public void GetValue_UnknownConfig_ThrowsNotFound()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        Assert.Throws<NotFoundException>(() => client.Config.GetValue("nope", "k"));
    }

    [Fact]
    public void GetValue_UnknownKey_ThrowsKeyNotFound()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        Assert.Throws<KeyNotFoundException>(() => client.Config.GetValue("billing", "missing"));
    }

    [Fact]
    public void GetValueOr_PresentKey_ReturnsValue()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        Assert.Equal(25, client.Config.GetValueOr("billing", "max_seats", 1));
        Assert.Equal("pro", client.Config.GetValueOr("billing", "tier", "free"));
        Assert.True(client.Config.GetValueOr("billing", "enabled", false));
    }

    [Fact]
    public async Task GetValueOr_MissingConfig_ReturnsDefault_AndRegisters()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        var value = client.Config.GetValueOr("brand-new", "ttl_ms", 500);
        Assert.Equal(500, value);
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("\"brand-new\"", lastBody);
        Assert.Contains("\"ttl_ms\"", lastBody);
        Assert.Contains("NUMBER", lastBody);
    }

    [Fact]
    public async Task GetValueOr_MissingKey_ReturnsDefault_AndRegisters()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            if (IsConfigsList(req)) return Json(ConfigListJson);
            return Json(EmptyListJson);
        });

        var value = client.Config.GetValueOr("billing", "new_field", "fallback");
        Assert.Equal("fallback", value);
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("new_field", lastBody);
    }

    [Fact]
    public async Task GetValueOr_RegisteredAsBoolean_WhenDefaultIsBool()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });
        _ = client.Config.GetValueOr("svc", "flag", true);
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("BOOLEAN", lastBody);
    }

    [Fact]
    public void GetValueOr_CoercesLongToInt()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        // max_seats=25 comes back as long; defaulted to int.
        var seats = client.Config.GetValueOr("billing", "max_seats", 1);
        Assert.Equal(25, seats);
        Assert.IsType<int>(seats);
    }

    [Fact]
    public void GetValueOr_NullStoredValue_ReturnsDefault()
    {
        // Cover the "raw is null → return default" branch by poking a null
        // into the resolved cache.
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing"); // force init
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["explicit_null"] = null;

        Assert.Equal("fb", client.Config.GetValueOr("billing", "explicit_null", "fb"));
    }

    [Fact]
    public void GetValueOr_UncoercibleValue_ReturnsDefault()
    {
        // Cover the "coerced is T t ? t : defaultValue" else branch by
        // forcing a non-coercible value into the cache.
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        // A list is not coercible to int via Convert.ChangeType.
        cache["billing"]["weird"] = new List<int> { 1, 2, 3 };

        Assert.Equal(99, client.Config.GetValueOr("billing", "weird", 99));
    }

    [Fact]
    public void GetValueOr_JsonElementCoercion_String()
    {
        // The resolver normalizes JsonElement on the standard path; this
        // exercises CoerceValue's JsonElement-unwrap fallback if a raw
        // element ever lands in the cache (single-config WS replace path
        // before normalization).
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["raw_str"] = System.Text.Json.JsonDocument.Parse("\"hello\"").RootElement;

        Assert.Equal("hello", client.Config.GetValueOr("billing", "raw_str", "fb"));
    }

    [Fact]
    public void GetValueOr_JsonElement_Null_ReturnsDefault()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["maybe_null"] = System.Text.Json.JsonDocument.Parse("null").RootElement;

        Assert.Equal(7, client.Config.GetValueOr("billing", "maybe_null", 7));
    }

    public enum Tier { Free, Pro }

    [Fact]
    public void GetValueOr_EnumFromString()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        Assert.Equal(Tier.Pro, client.Config.GetValueOr("billing", "tier", Tier.Free));
    }

    // ==================================================================
    // 8. ToSnakeCase helper edge cases
    // ==================================================================

    // ==================================================================
    // 9. Defensive coverage gaps
    // ==================================================================

    public class ThrowingGetterPoco
    {
        public int Plain { get; set; } = 5;
        public int Boom => throw new InvalidOperationException("getter explodes");
    }

    [Fact]
    public async Task Bind_Poco_ThrowingGetter_GracefullySkipped()
    {
        // Covers IterPocoItems's getter-throws catch: a property whose
        // getter throws is silently skipped during registration.
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json(EmptyListJson);
            }
            return Json(EmptyListJson);
        });

        var inst = client.Config.Bind("svc", new ThrowingGetterPoco());
        Assert.NotNull(inst);
        await client.Config.FlushAsync();
        Assert.NotNull(lastBody);
        // "plain" registered; "boom" skipped (would have raised).
        Assert.Contains("\"plain\"", lastBody);
    }

    public class ThrowingIntermediate
    {
        public Plan Inner => throw new InvalidOperationException("walk explodes");
    }

    [Fact]
    public void Bind_Poco_ThrowingIntermediateGetter_NoOpOnApply()
    {
        // Covers ApplyChangeToTarget's nested-walk getter-throws catch.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        // Bind succeeds because IterPocoItems also swallows the getter.
        client.Config.Bind("svc", new ThrowingIntermediate());

        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, Dictionary<string, object?>>(),
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["svc"] = new() { ["inner.max_seats"] = 9L },
            },
            "websocket",
        });
        // No exception — handled silently.
    }

    [Fact]
    public void Bind_Poco_BackingFieldAssignWrongType_GracefullySkipped()
    {
        // Cache an uncoercible value for an int property — both the setter
        // and the backing-field assignment throw; the catch on line 705
        // absorbs the failure so we don't surface a TargetException to
        // the WebSocket dispatch thread.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var inst = client.Config.Bind("svc", new Billing());

        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        // A List<int> is not convertible to int; CoerceValue gives up and
        // returns the original value; SetValue and backing-field SetValue
        // both then throw — exercising both catches.
        method.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, Dictionary<string, object?>>(),
            new Dictionary<string, Dictionary<string, object?>>
            {
                ["svc"] = new() { ["max_seats"] = new List<int> { 1, 2, 3 } },
            },
            "websocket",
        });
        // MaxSeats unchanged.
        Assert.Equal(5, inst.MaxSeats);
    }

    [Fact]
    public void Bind_ConcurrentCallsForSameId_AllReturnSameInstance()
    {
        // Covers the inner-lock idempotency check (the race-safe second
        // TryGetValue inside Bind): under contention, exactly one binding
        // wins and every caller receives that same instance.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        var instances = new System.Collections.Concurrent.ConcurrentBag<Billing>();
        Parallel.For(0, 32, _ =>
        {
            var b = client.Config.Bind("billing", new Billing());
            instances.Add(b);
        });
        var distinct = instances.Distinct().Count();
        Assert.Equal(1, distinct);
    }

    [Fact]
    public void Bind_Parent_AfterFullScan_NotFound_Throws()
    {
        // Covers the foreach completion path in ConfigIdFor — the loop
        // walks every binding without finding a match.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(EmptyListJson)));
        client.Config.Bind("a", new Plan());
        client.Config.Bind("b", new Billing());
        Assert.Throws<ArgumentException>(() =>
            client.Config.Bind("c", new Billing(), parent: new Plan()));
    }

    [Fact]
    public void GetValueOr_JsonElement_Number_UnwrappedAndCoerced()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["raw_num"] = System.Text.Json.JsonDocument.Parse("42").RootElement;

        Assert.Equal(42L, client.Config.GetValueOr("billing", "raw_num", 0L));
    }

    [Fact]
    public void GetValueOr_JsonElement_True_UnwrappedToBool()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["raw_t"] = System.Text.Json.JsonDocument.Parse("true").RootElement;

        Assert.True(client.Config.GetValueOr("billing", "raw_t", false));
    }

    [Fact]
    public void GetValueOr_JsonElement_False_UnwrappedToBool()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["raw_f"] = System.Text.Json.JsonDocument.Parse("false").RootElement;

        Assert.False(client.Config.GetValueOr("billing", "raw_f", true));
    }

    [Fact]
    public void GetValueOr_JsonElement_Array_PassesThrough()
    {
        // Default-arm: a JsonElement whose kind isn't one of the unwrapped
        // primitives passes through unchanged, so the int default applies.
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsList(req)) return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json(EmptyListJson));
        });
        _ = client.Config.Subscribe("billing");
        var cacheField = typeof(ConfigClient).GetField("_configCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        cache["billing"]["arr"] = System.Text.Json.JsonDocument.Parse("[1,2,3]").RootElement;

        Assert.Equal(7, client.Config.GetValueOr("billing", "arr", 7));
    }

    [Fact]
    public void ToSnakeCase_HandlesAcronymsAndEmptyAndSingleLetter()
    {
        var method = typeof(ConfigClient).GetMethod("ToSnakeCase",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        string Invoke(string s) => (string)method.Invoke(null, new object[] { s })!;

        Assert.Equal("", Invoke(""));
        Assert.Equal("x", Invoke("x"));
        Assert.Equal("max_seats", Invoke("MaxSeats"));
        Assert.Equal("ap_iv2", Invoke("ApIv2"));
        Assert.Equal("io", Invoke("IO"));
        Assert.Equal("http_request", Invoke("HttpRequest"));
    }
}
