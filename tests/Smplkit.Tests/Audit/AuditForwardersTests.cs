using System.Net;
using System.Text;
using Smplkit.Audit;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Tests for the SIEM forwarders + functions wrapper surface.
///
/// <para>Stubs the audit service via <see cref="MockHttpMessageHandler"/>;
/// no real network. Coverage on the new wrapper code must reach 100%
/// to satisfy the SDK CI gate.</para>
/// </summary>
public class AuditForwardersTests
{
    private static readonly Guid FwdId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid DeliveryId = Guid.Parse("22222222-3333-4444-5555-666666666666");

    private static (GenAudit.AuditClient gen, MockHttpMessageHandler mock) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenAudit.AuditClient("https://audit.example.com", http) { ReadResponseAsString = true };
        return (gen, mock);
    }

    private static StringContent JsonApi(string body) =>
        new(body, Encoding.UTF8, "application/vnd.api+json");

    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static string ForwarderResource(string name = "Datadog production", string slug = "datadog_production") =>
        "{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
            + "\"name\":\"" + name + "\",\"slug\":\"" + slug + "\","
            + "\"forwarder_type\":\"DATADOG\",\"enabled\":true,"
            + "\"http\":{\"method\":\"POST\",\"url\":\"https://siem.example.com/in\","
            + "\"headers\":[{\"name\":\"DD-API-KEY\",\"value\":\"<redacted>\"}],"
            + "\"success_status\":\"2xx\"},"
            + "\"data\":{},\"created_at\":\"2026-05-07T12:00:00Z\","
            + "\"updated_at\":\"2026-05-07T12:00:00Z\",\"version\":1}}";

    private static string DeliveryResource(string status = "SUCCEEDED") =>
        "{\"id\":\"" + DeliveryId + "\",\"type\":\"forwarder_delivery\",\"attributes\":{"
            + "\"forwarder_id\":\"" + FwdId + "\","
            + "\"event_id\":\"33333333-4444-5555-6666-777777777777\","
            + "\"attempt_number\":1,\"status\":\"" + status + "\","
            + "\"request\":{\"method\":\"POST\",\"url\":\"https://x\","
            + "\"headers\":[{\"name\":\"X-K\",\"value\":\"<redacted>\"}]},"
            + "\"response_status\":202,\"response_body\":\"ok\","
            + "\"latency_ms\":42,\"created_at\":\"2026-05-07T12:00:01Z\"}}";

    // ----------------------------------------------------------------------
    // CRUD
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsForwarder()
    {
        string? capturedBody = null;
        string? capturedMethod = null;
        var (gen, _) = MakeGen(async req =>
        {
            capturedMethod = req.Method.Method;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        });
        await using var client = new AuditClient(gen);
        var fwd = await client.Forwarders.CreateAsync(new CreateForwarderInput
        {
            Name = "Datadog production",
            ForwarderType = ForwarderType.Datadog,
            Http = new ForwarderHttp
            {
                Url = "https://siem.example.com/in",
                Headers = new List<HttpHeader> { new("DD-API-KEY", "real-secret") },
            },
            Filter = new Dictionary<string, object?> { ["=="] = new[] { 1, 1 } },
            Transform = "$",
            Data = new Dictionary<string, object?> { ["team"] = "platform" },
        });
        Assert.Equal("datadog_production", fwd.Slug);
        Assert.Equal("POST", capturedMethod);
        Assert.Contains("real-secret", capturedBody!);
    }

    [Fact]
    public async Task Create_ThrowsOnNullInput()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using var client = new AuditClient(gen);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Forwarders.CreateAsync(null!));
    }

    [Fact]
    public async Task Create_402ThrowsApiException()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = JsonApi("{\"errors\":[{\"status\":\"402\"}]}"),
        }));
        await using var client = new AuditClient(gen);
        await Assert.ThrowsAsync<GenAudit.ApiException>(() =>
            client.Forwarders.CreateAsync(new CreateForwarderInput
            {
                Name = "x",
                ForwarderType = ForwarderType.Http,
                Http = new ForwarderHttp { Url = "https://x" },
            }));
    }

    [Fact]
    public async Task List_PaginatesAndExtractsCursor()
    {
        var calls = 0;
        var (gen, _) = MakeGen(req =>
        {
            calls++;
            var body = calls == 1
                ? "{\"data\":[" + ForwarderResource("A", "a")
                    + "],\"meta\":{\"page_size\":1},"
                    + "\"links\":{\"next\":\"/api/v1/forwarders?page[size]=1&page[after]=tok-2\"}}"
                : "{\"data\":[" + ForwarderResource("B", "b") + "],\"meta\":{\"page_size\":1}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi(body),
            });
        });
        await using var client = new AuditClient(gen);
        var first = await client.Forwarders.ListAsync(new ListForwardersInput
        {
            ForwarderType = ForwarderType.Datadog,
            Enabled = true,
            PageSize = 1,
        });
        Assert.Single(first.Forwarders);
        Assert.Equal("tok-2", first.NextCursor);
        var second = await client.Forwarders.ListAsync(new ListForwardersInput { PageAfter = first.NextCursor });
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public async Task List_DefaultInputAndNoLinks()
    {
        // Covers the (input ??= new) branch and the (Links == null) cursor branch.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":1}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Forwarders.ListAsync();
        Assert.Empty(page.Forwarders);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Get_Success()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
        }));
        await using var client = new AuditClient(gen);
        var fwd = await client.Forwarders.GetAsync(FwdId);
        Assert.Equal(FwdId, fwd.Id);
        Assert.Single(fwd.Http.Headers);
        Assert.Equal("<redacted>", fwd.Http.Headers[0].Value);
    }

    [Fact]
    public async Task Update_Success()
    {
        string? method = null;
        var (gen, _) = MakeGen(req =>
        {
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource("Renamed", "renamed") + "}"),
            });
        });
        await using var client = new AuditClient(gen);
        var fwd = await client.Forwarders.UpdateAsync(FwdId, new CreateForwarderInput
        {
            Name = "Renamed",
            ForwarderType = ForwarderType.Datadog,
            Http = new ForwarderHttp { Url = "https://x" },
        });
        Assert.Equal("PUT", method);
        Assert.Equal("Renamed", fwd.Name);
    }

    [Fact]
    public async Task Update_ThrowsOnNullInput()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using var client = new AuditClient(gen);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Forwarders.UpdateAsync(FwdId, null!));
    }

    [Fact]
    public async Task Delete_Success()
    {
        string? method = null;
        var (gen, _) = MakeGen(req =>
        {
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        await using var client = new AuditClient(gen);
        await client.Forwarders.DeleteAsync(FwdId);
        Assert.Equal("DELETE", method);
    }

    // ----------------------------------------------------------------------
    // Deliveries
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ListDeliveries_FiltersAndPaginates()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[" + DeliveryResource() + "],\"meta\":{\"page_size\":1}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Forwarders.ListDeliveriesAsync(FwdId, new ListDeliveriesInput
        {
            Status = "SUCCEEDED",
            CreatedAtRange = "[2020-01-01T00:00:00Z,*)",
            PageSize = 1,
        });
        Assert.Single(page.Deliveries);
        Assert.Equal("SUCCEEDED", page.Deliveries[0].Status);
    }

    [Fact]
    public async Task ListDeliveries_DefaultInput()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"page_size\":1}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Forwarders.ListDeliveriesAsync(FwdId);
        Assert.Empty(page.Deliveries);
    }

    [Fact]
    public async Task ListDeliveries_FilterEventId_PassesQueryParam()
    {
        string? capturedUrl = null;
        var eventId = Guid.Parse("33333333-4444-5555-6666-777777777777");
        var (gen, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[" + DeliveryResource() + "],\"meta\":{\"page_size\":1}}"),
            });
        });
        await using var client = new AuditClient(gen);
        var page = await client.Forwarders.ListDeliveriesAsync(FwdId, new ListDeliveriesInput
        {
            EventId = eventId,
        });
        Assert.Single(page.Deliveries);
        Assert.Contains(eventId.ToString(), capturedUrl!);
    }

    [Fact]
    public async Task RetryDelivery_ReturnsNewRow()
    {
        var (gen, _) = MakeGen(req =>
        {
            Assert.Contains("actions/retry", req.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + DeliveryResource("SUCCEEDED") + "}"),
            });
        });
        await using var client = new AuditClient(gen);
        var row = await client.Forwarders.RetryDeliveryAsync(FwdId, DeliveryId);
        // Touch every record property so coverlet sees the auto-generated
        // accessors as exercised — record positional ctors emit one
        // accessor per parameter and each is tracked separately.
        Assert.Equal(DeliveryId, row.Id);
        Assert.Equal(FwdId, row.ForwarderId);
        Assert.Equal(Guid.Parse("33333333-4444-5555-6666-777777777777"), row.EventId);
        Assert.Equal(1, row.AttemptNumber);
        Assert.Equal("SUCCEEDED", row.Status);
        Assert.NotNull(row.Request);
        Assert.Equal(202, row.ResponseStatus);
        Assert.Equal("ok", row.ResponseBody);
        Assert.Equal(42, row.LatencyMs);
        Assert.Null(row.Error);
        Assert.NotNull(row.CreatedAt);
    }

    [Fact]
    public async Task RetryFailedDeliveries_Summary()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"attempted\":3,\"succeeded\":2,\"failed\":1}"),
        }));
        await using var client = new AuditClient(gen);
        var s = await client.Forwarders.RetryFailedDeliveriesAsync(FwdId);
        Assert.Equal(3, s.Attempted);
        Assert.Equal(2, s.Succeeded);
        Assert.Equal(1, s.Failed);
    }

    // ----------------------------------------------------------------------
    // functions.test_forwarder
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ExecuteTestForwarder_Success()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("{\"succeeded\":true,\"response_status\":202,"
                + "\"response_headers\":{\"X-Echo\":\"y\"},"
                + "\"response_body\":\"accepted\","
                + "\"latency_ms\":12,\"error\":null}"),
        }));
        await using var client = new AuditClient(gen);
        var r = await client.Functions.ExecuteTestForwarderAsync(new TestForwarderInput
        {
            Url = "https://siem.example.com/in",
            Body = "{\"hello\":\"world\"}",
            TimeoutMs = 5000,
            Headers = new List<HttpHeader> { new("X-K", "v") },
        });
        Assert.True(r.Succeeded);
        Assert.Equal(202, r.ResponseStatus);
        Assert.Equal("accepted", r.ResponseBody);
        Assert.Equal("y", r.ResponseHeaders["X-Echo"]);
    }

    [Fact]
    public async Task ExecuteTestForwarder_ThrowsOnNullInput()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using var client = new AuditClient(gen);
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => client.Functions.ExecuteTestForwarderAsync(null!));
    }

    [Fact]
    public async Task ExecuteTestForwarder_NullResponseBody()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("{\"succeeded\":false,\"response_status\":null,"
                + "\"response_headers\":{},\"latency_ms\":null,\"error\":\"x\"}"),
        }));
        await using var client = new AuditClient(gen);
        var r = await client.Functions.ExecuteTestForwarderAsync(new TestForwarderInput { Url = "https://x" });
        Assert.False(r.Succeeded);
        Assert.Equal(string.Empty, r.ResponseBody);
        Assert.Equal("x", r.Error);
    }

    // ----------------------------------------------------------------------
    // do_not_forward
    // ----------------------------------------------------------------------

    [Fact]
    public async Task EventsRecord_PassesDoNotForward()
    {
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\","
                    + "\"type\":\"event\",\"attributes\":{\"action\":\"u.created\","
                    + "\"resource_type\":\"u\",\"resource_id\":\"1\","
                    + "\"do_not_forward\":true,"
                    + "\"occurred_at\":\"2026-05-07T12:00:00Z\","
                    + "\"created_at\":\"2026-05-07T12:00:01Z\","
                    + "\"actor_type\":\"API_KEY\",\"actor_label\":\"\","
                    + "\"data\":{},\"idempotency_key\":\"\"}}}"),
            };
        });
        await using var client = new AuditClient(gen);
        client.Events.Record(new CreateEventInput
        {
            Action = "u.created",
            ResourceType = "u",
            ResourceId = "1",
            DoNotForward = true,
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(capturedBody);
        Assert.Contains("\"do_not_forward\":true", capturedBody!);
    }

    // ----------------------------------------------------------------------
    // Coverage corner cases for the conversion helpers
    // ----------------------------------------------------------------------

    [Fact]
    public void RecordAccessors_FullyCovered()
    {
        // Touch every positional record accessor so coverlet sees the
        // auto-generated getters as exercised. Records emit one accessor
        // per parameter; per-class coverage requires each to be read.
        var ev = new AuditEvent(
            Guid.Empty, "a", "rt", "rid",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "USER", null, "label",
            new Dictionary<string, object?>(), "k", true);
        Assert.True(ev.DoNotForward);

        var fwd = new Forwarder(
            FwdId, "n", "s", ForwarderType.Http, true,
            new Dictionary<string, object?>(), "tx",
            new ForwarderHttp { Url = "u" },
            new Dictionary<string, object?>(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        Assert.Equal(ForwarderType.Http, fwd.ForwarderType);
        Assert.True(fwd.Enabled);
        Assert.NotNull(fwd.Filter);
        Assert.Equal("tx", fwd.Transform);
        Assert.Empty(fwd.Data);
        Assert.NotNull(fwd.CreatedAt);
        Assert.NotNull(fwd.UpdatedAt);
        Assert.NotNull(fwd.DeletedAt);
        Assert.Equal(1, fwd.Version);

        var tr = new TestForwarderResult(
            true, 200, new Dictionary<string, object?>().ToDictionary(_ => "", _ => ""),
            "body", 12, null);
        Assert.Equal(12, tr.LatencyMs);
    }

    [Fact]
    public async Task ConvertJson_HandlesIDictionaryStringObject()
    {
        // The Filter field is deserialized by NSwag as
        // IDictionary<string, object> — the wrapper has a branch that
        // converts that into IDictionary<string, object?>.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"filter\":{\"==\":[1,1]},"
                + "\"data\":{\"team\":\"platform\",\"count\":42,\"on\":true,\"off\":false,"
                + "\"nested\":{\"k\":\"v\"},\"items\":[1,2.5,\"three\",null]}}}}"),
        }));
        await using var client = new AuditClient(gen);
        var fwd = await client.Forwarders.GetAsync(FwdId);
        Assert.NotNull(fwd.Filter);
        Assert.NotNull(fwd.Data);
        Assert.Equal("platform", fwd.Data["team"]);
        Assert.Equal(42L, fwd.Data["count"]);
        Assert.Equal(true, fwd.Data["on"]);
        Assert.Equal(false, fwd.Data["off"]);
    }

    [Fact]
    public async Task ConvertJson_NonObjectJsonElementFallsThroughToNull()
    {
        // When the wire value for a free-form object field is anything
        // other than a JSON object (a scalar, an array, etc.), NSwag
        // still hands us a JsonElement and ConvertJson must safely fall
        // through to null. Send filter as a plain string to trigger.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"filter\":\"not-an-object\"}}}"),
        }));
        await using var client = new AuditClient(gen);
        var fwd = await client.Forwarders.GetAsync(FwdId);
        Assert.Null(fwd.Filter);
    }

    [Fact]
    public async Task ForwarderResource_HandlesMinimalAttributes()
    {
        // Bare-minimum attributes — exercises the (Http == null) and
        // (Headers/Filter/Data == null) branches in the converters.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":false}}}"),
        }));
        await using var client = new AuditClient(gen);
        var fwd = await client.Forwarders.GetAsync(FwdId);
        Assert.Equal(string.Empty, fwd.Http.Url);
        Assert.Empty(fwd.Http.Headers);
        Assert.Empty(fwd.Data);
    }

    // ----------------------------------------------------------------------
    // ForwarderType — wire round-trip + extension methods
    //
    // The wrapper has two enum surfaces: the public Smplkit.Audit.ForwarderType
    // (what customers see) and the codegen's internal one. Round-tripping
    // every value through Create+Get exercises BOTH switch arms in
    // AuditForwarders.ToGenForwarderType / FromGenForwarderType.
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(ForwarderType.Http, "HTTP")]
    [InlineData(ForwarderType.Datadog, "DATADOG")]
    [InlineData(ForwarderType.SplunkHec, "SPLUNK_HEC")]
    [InlineData(ForwarderType.SumoLogic, "SUMO_LOGIC")]
    [InlineData(ForwarderType.NewRelic, "NEW_RELIC")]
    [InlineData(ForwarderType.Honeycomb, "HONEYCOMB")]
    [InlineData(ForwarderType.Elastic, "ELASTIC")]
    public async Task ForwarderType_RoundTripsThroughCreateAndGet(ForwarderType type, string wire)
    {
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
            }
            // Server echoes the same wire value back on read.
            return new HttpResponseMessage(req.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                    + "\"name\":\"n\",\"slug\":\"n\",\"forwarder_type\":\"" + wire + "\",\"enabled\":true,"
                    + "\"http\":{\"method\":\"POST\",\"url\":\"u\",\"headers\":[],\"success_status\":\"2xx\"},"
                    + "\"data\":{}}}}"),
            };
        });
        await using var client = new AuditClient(gen);

        var created = await client.Forwarders.CreateAsync(new CreateForwarderInput
        {
            Name = "n",
            ForwarderType = type,
            Http = new ForwarderHttp { Url = "u" },
        });
        // POST body went out — read converts back to the typed wrapper
        // enum (FromGen converter chose the matching arm). The wire
        // serialization of the codegen enum is delegated to the codegen's
        // JsonStringEnumConverter; we don't pin its exact spelling here.
        Assert.NotNull(capturedBody);
        Assert.Equal(type, created.ForwarderType);
    }

    [Fact]
    public void ForwarderTypeExtensions_ToWireValue_RoundTrips()
    {
        Assert.Equal("HTTP", ForwarderType.Http.ToWireValue());
        Assert.Equal("SPLUNK_HEC", ForwarderType.SplunkHec.ToWireValue());
        Assert.Equal("ELASTIC", ForwarderType.Elastic.ToWireValue());
    }

    [Theory]
    [InlineData("HTTP", ForwarderType.Http)]
    [InlineData("DATADOG", ForwarderType.Datadog)]
    [InlineData("SPLUNK_HEC", ForwarderType.SplunkHec)]
    [InlineData("SUMO_LOGIC", ForwarderType.SumoLogic)]
    [InlineData("NEW_RELIC", ForwarderType.NewRelic)]
    [InlineData("HONEYCOMB", ForwarderType.Honeycomb)]
    [InlineData("ELASTIC", ForwarderType.Elastic)]
    public void ForwarderTypeExtensions_FromWireValue_AcceptsKnown(string wire, ForwarderType expected)
    {
        Assert.Equal(expected, ForwarderTypeExtensions.FromWireValue(wire));
    }

    [Fact]
    public void ForwarderTypeExtensions_FromWireValue_ThrowsOnUnknown()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => ForwarderTypeExtensions.FromWireValue("definitely-not-a-real-type"));
        Assert.Contains("Unknown ForwarderType", ex.Message);
    }

    [Fact]
    public async Task ForwarderType_ConverterDefaultArmsThrowOnOutOfRange()
    {
        // The wrapper's switch converters in AuditForwarders include a
        // defensive default arm that throws on an unrecognized enum
        // value. Cover both arms by feeding them an out-of-range cast:
        //
        //   ToGen: pass (ForwarderType)999 through Create.
        //   FromGen: respond with a forwarder_type the codegen
        //            deserializes to an out-of-range value (we feed it
        //            a malformed string the codegen rejects, surfacing
        //            an exception before our converter sees it; cover
        //            the FromGen default arm by direct invocation via
        //            reflection on the private static method).
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        await using var client = new AuditClient(gen);

        // ToGen default arm.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.Forwarders.CreateAsync(new CreateForwarderInput
            {
                Name = "n",
                ForwarderType = (ForwarderType)999,
                Http = new ForwarderHttp { Url = "u" },
            }));

        // FromGen default arm — invoke via reflection on the private
        // static method with an out-of-range codegen enum value.
        var method = typeof(AuditForwarders).GetMethod(
            "FromGenForwarderType",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method!.Invoke(null, new object[] { (GenAudit.ForwarderType)999 }));
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }
}
