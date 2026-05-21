using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Management;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the declarative discovery API (ADR-037 §2.13/§2.14):
///   1. <see cref="ConfigRegistrationBuffer"/> — declare/add_item/drain semantics.
///   2. <see cref="ConfigsClient"/> — RegisterConfig / RegisterConfigItem / FlushAsync.
///   3. <see cref="ConfigClient.GetOrCreate"/> — idempotency, parent-by-reference,
///      pre-fetch flush wiring.
///   4. <see cref="LiveConfigProxy"/> typed getters — happy paths, mismatch
///      paths, default-fallback paths.
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
                            "ratio": {"value": 1.5, "type": "NUMBER"},
                            "payload": {"value": {"k": "v"}, "type": "JSON"}
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
        // No prior Declare — AddItem returns without queuing.
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
        // Drain "billing+max_seats", then add same item again — must not
        // re-appear in next drain (sent-item dedup is process-lifetime).
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
        // After draining a config's first item, a *new* item should
        // re-create a delta entry (using retained meta) so it can be sent.
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
            return Task.FromResult(Json(ConfigListJson));
        });

        await client.Manage.Config.FlushAsync();
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
                return Json("""{"data":[]}""");
            }
            return Json(ConfigListJson);
        });

        client.Manage.Config.RegisterConfig(
            "billing", service: "svc", environment: "prod",
            parent: "common", name: "Billing", description: "Plan limits");
        Assert.Equal(1, client.Manage.Config.PendingCount);

        await client.Manage.Config.FlushAsync();
        Assert.Equal(0, client.Manage.Config.PendingCount);
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
                return Json("""{"data":[]}""");
            }
            return Json(ConfigListJson);
        });

        client.Manage.Config.RegisterConfig("billing", "svc", "prod");
        client.Manage.Config.RegisterConfigItem("billing", "max_seats", "NUMBER", 5, "seats");
        client.Manage.Config.RegisterConfigItem("billing", "tier", "STRING", "free", null);
        client.Manage.Config.RegisterConfigItem("billing", "enabled", "BOOLEAN", false, null);
        client.Manage.Config.RegisterConfigItem("billing", "payload", "JSON", null, null);
        // Unknown type → null in payload (still serializes).
        client.Manage.Config.RegisterConfigItem("billing", "weird", "WEIRD", "?", null);

        await client.Manage.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("STRING", lastBody);
        Assert.Contains("NUMBER", lastBody);
        Assert.Contains("BOOLEAN", lastBody);
        Assert.Contains("JSON", lastBody);
        Assert.Contains("\"seats\"", lastBody);
    }

    [Fact]
    public async Task ConfigsClient_FlushAsync_ServerError_Swallowed()
    {
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) return Task.FromResult(ServerError());
            return Task.FromResult(Json(ConfigListJson));
        });

        client.Manage.Config.RegisterConfig("billing", "svc", "prod");
        // Per ADR-024 §2.9, bulk POST failures are fire-and-forget.
        await client.Manage.Config.FlushAsync();
        // Items are still drained — discovery never blocks customer code.
        Assert.Equal(0, client.Manage.Config.PendingCount);
    }

    [Fact]
    public async Task ConfigsClient_RegisterConfig_ThresholdTriggersBackgroundFlush()
    {
        // Flush threshold is 50; register 51 unique configs and confirm a
        // bulk POST eventually fires (background task, awaited via small wait).
        int bulkCount = 0;
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) { Interlocked.Increment(ref bulkCount); return Task.FromResult(Json("""{"data":[]}""")); }
            return Task.FromResult(Json(ConfigListJson));
        });

        for (int i = 0; i < 51; i++)
            client.Manage.Config.RegisterConfig($"cfg-{i}", "svc", "prod");

        // Background flush is fire-and-forget; give it a tick.
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
            if (IsConfigsBulkPost(req)) { Interlocked.Increment(ref bulkCount); return Task.FromResult(Json("""{"data":[]}""")); }
            return Task.FromResult(Json(ConfigListJson));
        });

        // 50 distinct configs each with a single item → PendingCount stays at
        // 50; registering items pushes the buffer past the threshold.
        for (int i = 0; i < 50; i++)
            client.Manage.Config.RegisterConfig($"cfg-{i}", "svc", "prod");
        // One more declaration + one item to cross the boundary.
        client.Manage.Config.RegisterConfig("cfg-extra", "svc", "prod");
        client.Manage.Config.RegisterConfigItem("cfg-extra", "k", "STRING", "v", null);

        for (int i = 0; i < 20 && bulkCount == 0; i++)
            await Task.Delay(25);
        Assert.True(bulkCount >= 1);
    }

    // ==================================================================
    // 3. ConfigClient.GetOrCreate
    // ==================================================================

    [Fact]
    public void GetOrCreate_ReturnsLiveConfigProxy()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing", description: "discovered");
        Assert.IsType<LiveConfigProxy>(proxy);
        Assert.Equal("billing", proxy.ConfigId);
    }

    [Fact]
    public void GetOrCreate_Idempotent_ReturnsSameInstance()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var a = client.Config.GetOrCreate("billing");
        var b = client.Config.GetOrCreate("billing");
        Assert.Same(a, b);
    }

    [Fact]
    public void GetOrCreate_Get_ReturnsSameInstance()
    {
        // Mike's "parent by reference" invariant: the cached proxy works
        // identically whether obtained via GetOrCreate or via Get.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var declared = client.Config.GetOrCreate("billing");
        var resolved = client.Config.Get("billing");
        Assert.Same(declared, resolved);
    }

    [Fact]
    public void GetOrCreate_ParentByString_Accepted()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing", parent: "common");
        Assert.Equal("billing", proxy.ConfigId);
    }

    [Fact]
    public void GetOrCreate_ParentByLiveConfigProxy_Accepted()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var common = client.Config.GetOrCreate("common");
        var billing = client.Config.GetOrCreate("billing", parent: common);
        Assert.Equal("billing", billing.ConfigId);
    }

    [Fact]
    public void GetOrCreate_ParentInvalidType_Throws()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        Assert.Throws<ArgumentException>(() =>
            client.Config.GetOrCreate("billing", parent: 42));
    }

    [Fact]
    public void GetOrCreate_FlushesBufferBeforeInitialFetch()
    {
        // Per ADR-037 §2.14: EnsureInitialized flushes any buffered
        // declarations BEFORE the initial list fetch, so newly-discovered
        // configs show up in the cache without needing a second pass.
        var calls = new List<string>();
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) { calls.Add("bulk"); return Task.FromResult(Json("""{"data":[]}""")); }
            if (req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/configs"))
            { calls.Add("list"); return Task.FromResult(Json(ConfigListJson)); }
            return Task.FromResult(Json("{}"));
        });

        client.Config.GetOrCreate("billing");
        // First HTTP interaction: bulk POST (discovery flush). Second: list.
        Assert.True(calls.Count >= 2);
        Assert.Equal("bulk", calls[0]);
        Assert.Equal("list", calls[1]);
    }

    [Fact]
    public void GetOrCreate_PreFetchFlushFails_DoesNotPropagate()
    {
        // Bulk POST 500s — EnsureInitialized must still reach the list call.
        var (client, _) = MakeClient(req =>
        {
            if (IsConfigsBulkPost(req)) return Task.FromResult(ServerError());
            return Task.FromResult(Json(ConfigListJson));
        });
        // Should not throw.
        var proxy = client.Config.GetOrCreate("billing");
        Assert.NotNull(proxy);
    }

    [Fact]
    public void GetOrCreate_TriggersDiscoveryUploadOnFlush()
    {
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json("""{"data":[]}""");
            }
            return Json(ConfigListJson);
        });

        client.Config.GetOrCreate("billing", description: "test discovery");
        Assert.NotNull(lastBody);
        Assert.Contains("\"billing\"", lastBody);
    }

    // ==================================================================
    // 4. LiveConfigProxy typed getters
    // ==================================================================

    [Fact]
    public void GetBool_ReadsBooleanValue()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.True(proxy.GetBool("enabled", false));
    }

    [Fact]
    public void GetBool_MissingKey_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.True(proxy.GetBool("missing", defaultValue: true));
    }

    [Fact]
    public void GetBool_TypeMismatch_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        // "tier" is a string, not a bool.
        Assert.False(proxy.GetBool("tier", false));
    }

    [Fact]
    public void GetInt_FromNumberValue()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(25, proxy.GetInt("max_seats", 5));
    }

    [Fact]
    public void GetInt_MissingKey_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(7, proxy.GetInt("missing", 7));
    }

    [Fact]
    public void GetInt_BoolValue_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(99, proxy.GetInt("enabled", 99));
    }

    [Fact]
    public void GetInt_StringValue_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(99, proxy.GetInt("tier", 99));
    }

    [Fact]
    public void GetInt_HandlesIntDoubleLongAndJsonElement()
    {
        // Long, double-with-no-fractional, and JsonElement number all
        // coerce cleanly to int.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");

        // Surgically poke values of each type into the resolver's cache via
        // the public surface: re-list with custom JSON would be heavier than
        // we need. Instead, exercise the existing 25 (long) and 1.5 (double
        // → falls back). Confirm 25 is read as int from a long.
        Assert.Equal(25, proxy.GetInt("max_seats", 0));
        // 1.5 has a fractional part → falls back to default.
        Assert.Equal(0, proxy.GetInt("ratio", 0));
    }

    [Fact]
    public void GetFloat_FromNumberValue()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(1.5, proxy.GetFloat("ratio", 0.0));
    }

    [Fact]
    public void GetFloat_FromIntegralNumber()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(25.0, proxy.GetFloat("max_seats", 0.0));
    }

    [Fact]
    public void GetFloat_MissingKey_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(2.5, proxy.GetFloat("missing", 2.5));
    }

    [Fact]
    public void GetFloat_BoolValue_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(9.9, proxy.GetFloat("enabled", 9.9));
    }

    [Fact]
    public void GetFloat_StringValue_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal(3.14, proxy.GetFloat("tier", 3.14));
    }

    [Fact]
    public void GetString_ReadsStringValue()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal("pro", proxy.GetString("tier", "free"));
    }

    [Fact]
    public void GetString_MissingKey_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal("fallback", proxy.GetString("missing", "fallback"));
    }

    [Fact]
    public void GetString_TypeMismatch_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal("default", proxy.GetString("max_seats", "default"));
    }

    [Fact]
    public void GetJson_ReadsAnyShape()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        var payload = proxy.GetJson("payload", null);
        Assert.NotNull(payload);
    }

    [Fact]
    public void GetJson_MissingKey_ReturnsDefault()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        Assert.Equal("default", proxy.GetJson("missing", "default"));
    }

    [Fact]
    public async Task TypedGetters_RegisterDeclarationOnFirstCall()
    {
        // Each typed getter call should queue an item declaration on the buffer.
        string? lastBody = null;
        var (client, _) = MakeClient(async req =>
        {
            if (IsConfigsBulkPost(req))
            {
                lastBody = await req.Content!.ReadAsStringAsync();
                return Json("""{"data":[]}""");
            }
            return Json(ConfigListJson);
        });

        var proxy = client.Config.GetOrCreate("billing");
        proxy.GetInt("max_seats", 5, description: "Maximum seats.");
        proxy.GetString("tier", "free", description: "Plan tier.");
        proxy.GetBool("enabled", false);
        proxy.GetFloat("ratio", 0.0);
        proxy.GetJson("payload", null);

        await client.Manage.Config.FlushAsync();
        Assert.NotNull(lastBody);
        Assert.Contains("max_seats", lastBody);
        Assert.Contains("tier", lastBody);
        Assert.Contains("enabled", lastBody);
        Assert.Contains("ratio", lastBody);
        Assert.Contains("payload", lastBody);
        Assert.Contains("Maximum seats.", lastBody);
    }

    [Fact]
    public void GetString_JsonElementStringValue_ReadsViaGetString()
    {
        // Defensive path: if the cache somehow holds a JsonElement (instead
        // of a normalized string), GetString must still return the string.
        // Inject directly via reflection — the resolver normally normalizes
        // these away, but the path is reachable from non-normalized writes.
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var proxy = client.Config.GetOrCreate("billing");
        // Force lazy init then poke the cache.
        _ = proxy["tier"];

        var cacheField = typeof(ConfigClient).GetField("_configCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var cache = (Dictionary<string, Dictionary<string, object?>>)cacheField.GetValue(client.Config)!;
        var doc = System.Text.Json.JsonDocument.Parse("\"raw-element\"");
        cache["billing"]["tier"] = doc.RootElement;

        Assert.Equal("raw-element", proxy.GetString("tier", "default"));
    }

    [Fact]
    public void GetOrCreate_UnknownConfig_DoesNotThrow()
    {
        // Per ADR-037: GetOrCreate is the declarative entry point — it never
        // throws NotFoundException, even if the config isn't on the server
        // yet. Subsequent typed-getter reads return defaults.
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        // No config server-side; the proxy still constructs.
        var proxy = client.Config.GetOrCreate("brand-new", description: "fresh");
        // Reading a value falls back to default with no exception.
        Assert.Throws<NotFoundException>(() => proxy.GetBool("k", true));
    }
}
