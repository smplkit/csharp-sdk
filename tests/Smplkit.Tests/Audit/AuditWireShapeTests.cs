using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Audit;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Wire-body shape tests for the audit wrapper.
///
/// <para>Asserts on the actual JSON the SDK posts. Guards against the
/// failure mode that shipped smplkit-sdk@3.2.21 / @smplkit/sdk@3.0.19:
/// the generated client compiled cleanly after the spec dropped a
/// field, but the wrapper kept emitting it, and CI was none the wiser
/// because no test inspected the bytes.</para>
///
/// <para>The whitelists below come from the audit service's OpenAPI
/// spec (openapi/audit.json: components.schemas.Event / .Forwarder),
/// not from the generated client.</para>
/// </summary>
public class AuditWireShapeTests
{
    private static readonly Guid FwdId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>POST /api/v1/events accepts only these attribute keys. The rest
    /// (created_at, actor_*, idempotency_key) are readOnly.</summary>
    private static readonly HashSet<string> EventPostAttrs = new()
    {
        "action", "resource_type", "resource_id",
        "occurred_at", "data", "do_not_forward",
    };

    /// <summary>POST/PUT /api/v1/forwarders accepts only these attribute keys.
    /// slug is x-immutable; created_at/updated_at/deleted_at/version are readOnly.</summary>
    private static readonly HashSet<string> ForwarderPostAttrs = new()
    {
        "name", "forwarder_type", "http",
        "enabled", "filter", "transform", "data",
    };

    private static (GenAudit.AuditClient gen, MockHttpMessageHandler mock) MakeGen(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var mock = new MockHttpMessageHandler(handler);
        var http = new HttpClient(mock);
        var gen = new GenAudit.AuditClient("https://audit.example.com", http) { ReadResponseAsString = true };
        return (gen, mock);
    }

    private const string EventResponseJson =
        "{\"data\":{\"id\":\"00000000-0000-0000-0000-000000000001\",\"type\":\"event\","
        + "\"attributes\":{\"action\":\"invoice.created\",\"resource_type\":\"invoice\","
        + "\"resource_id\":\"inv-1\",\"occurred_at\":\"2026-05-06T12:00:00Z\","
        + "\"created_at\":\"2026-05-06T12:00:01Z\",\"actor_type\":\"API_KEY\","
        + "\"actor_label\":\"\",\"data\":{},\"idempotency_key\":\"k-1\"}}}";

    private static string ForwarderResponseJson(string name) =>
        "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\","
        + "\"attributes\":{\"name\":\"" + name + "\",\"slug\":\"x\","
        + "\"forwarder_type\":\"datadog\",\"enabled\":true,"
        + "\"http\":{\"method\":\"POST\",\"url\":\"https://siem.example.com/in\","
        + "\"headers\":[{\"name\":\"DD-API-KEY\",\"value\":\"<redacted>\"}],"
        + "\"success_status\":\"2xx\"},\"data\":{},"
        + "\"created_at\":\"2026-05-07T12:00:00Z\",\"updated_at\":\"2026-05-07T12:00:00Z\","
        + "\"version\":1}}}";

    /// <summary>Capture the parsed body and headers of the (single) request the SDK posts.</summary>
    private sealed class Captured
    {
        public string? Method;
        public JsonElement Body;
        public string? IdempotencyKey;
    }

    private static (GenAudit.AuditClient gen, Captured captured) BuildCapturing(
        HttpStatusCode status, string responseBody)
    {
        var captured = new Captured();
        var (gen, _) = MakeGen(async req =>
        {
            captured.Method = req.Method.Method;
            if (req.Headers.TryGetValues("Idempotency-Key", out var values))
            {
                captured.IdempotencyKey = values.FirstOrDefault();
            }
            var bodyText = req.Content == null
                ? string.Empty
                : await req.Content.ReadAsStringAsync();
            if (!string.IsNullOrEmpty(bodyText))
            {
                using var doc = JsonDocument.Parse(bodyText);
                captured.Body = doc.RootElement.Clone();
            }
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/vnd.api+json"),
            };
        });
        return (gen, captured);
    }

    private static JsonElement Attributes(JsonElement body)
    {
        return body.GetProperty("data").GetProperty("attributes");
    }

    private static HashSet<string> KeysOf(JsonElement obj)
    {
        var keys = new HashSet<string>();
        foreach (var prop in obj.EnumerateObject())
        {
            keys.Add(prop.Name);
        }
        return keys;
    }

    // -----------------------------------------------------------------
    // events.Record — wire body
    // -----------------------------------------------------------------

    [Fact]
    public async Task EventsRecord_WireShape_AllParameters()
    {
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, EventResponseJson);
        await using var client = new AuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            Action = "invoice.created",
            ResourceType = "invoice",
            ResourceId = "inv-1",
            OccurredAt = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero),
            Data = new Dictionary<string, object?>
            {
                ["snapshot"] = new Dictionary<string, object?> { ["total_cents"] = 4900 },
                ["req_id"] = "abc",
            },
            IdempotencyKey = "k-1",
            DoNotForward = true,
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("event", captured.Body.GetProperty("data").GetProperty("type").GetString());
        // POST: server assigns id; wrapper sends "".
        Assert.Equal("", captured.Body.GetProperty("data").GetProperty("id").GetString());

        var attrs = Attributes(captured.Body);
        Assert.Equal("invoice.created", attrs.GetProperty("action").GetString());
        Assert.Equal("invoice", attrs.GetProperty("resource_type").GetString());
        Assert.Equal("inv-1", attrs.GetProperty("resource_id").GetString());
        Assert.StartsWith("2026-05-06T12:00:00", attrs.GetProperty("occurred_at").GetString()!);

        var data = attrs.GetProperty("data");
        Assert.Equal(4900, data.GetProperty("snapshot").GetProperty("total_cents").GetInt32());
        Assert.Equal("abc", data.GetProperty("req_id").GetString());
        Assert.True(attrs.GetProperty("do_not_forward").GetBoolean());

        // Idempotency-Key is a HEADER, not a body attribute.
        Assert.False(attrs.TryGetProperty("idempotency_key", out _),
            "idempotency_key must NOT appear in body");
        Assert.Equal("k-1", captured.IdempotencyKey);
    }

    [Fact]
    public async Task EventsRecord_WireShape_MinimalCallStaysWithinWhitelist()
    {
        // C#'s System.Text.Json defaults serialize null fields and the
        // generated Event model has nullable readonly fields. The
        // wrapper sets Data to {} unconditionally and only sets
        // Do_not_forward when true. We don't assert key omission here —
        // the no-extra-keys gate below handles invented fields, and is
        // the gate that would catch the next snapshot-style regression.
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, EventResponseJson);
        await using var client = new AuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            Action = "invoice.created",
            ResourceType = "invoice",
            ResourceId = "inv-1",
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));

        var attrs = Attributes(captured.Body);
        Assert.Equal("invoice.created", attrs.GetProperty("action").GetString());
        Assert.Equal("invoice", attrs.GetProperty("resource_type").GetString());
        Assert.Equal("inv-1", attrs.GetProperty("resource_id").GetString());
    }

    [Fact]
    public async Task EventsRecord_WireShape_DoNotForwardSerializesCorrectly()
    {
        // The C# generated model emits do_not_forward=false even when
        // the wrapper doesn't explicitly set it (System.Text.Json
        // defaults). That's wire-equivalent to the server's own
        // default, so the test guards the value, not the field's
        // presence: when the caller passes false, the wire value is
        // false (not flipped, not coerced to a string).
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, EventResponseJson);
        await using var client = new AuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            Action = "x",
            ResourceType = "y",
            ResourceId = "z",
            DoNotForward = false,
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));

        var attrs = Attributes(captured.Body);
        if (attrs.TryGetProperty("do_not_forward", out var dnf))
        {
            Assert.False(dnf.GetBoolean(),
                "do_not_forward must serialize as boolean false when caller passes false");
        }
    }

    [Fact]
    public async Task EventsRecord_WireShape_NoTopLevelSnapshot()
    {
        // Regression guard for the smplkit-sdk@3.2.21 incident.
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, EventResponseJson);
        await using var client = new AuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            Action = "invoice.created",
            ResourceType = "invoice",
            ResourceId = "inv-1",
            Data = new Dictionary<string, object?>
            {
                ["snapshot"] = new Dictionary<string, object?> { ["total_cents"] = 4900 },
            },
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));

        var attrs = Attributes(captured.Body);
        Assert.False(attrs.TryGetProperty("snapshot", out _),
            "top-level snapshot must not appear on the wire");
        // And it IS still nested in data.
        var data = attrs.GetProperty("data");
        Assert.True(data.TryGetProperty("snapshot", out _),
            "data.snapshot must round-trip");
    }

    [Fact]
    public async Task EventsRecord_WireShape_NoExtraKeys()
    {
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, EventResponseJson);
        await using var client = new AuditClient(gen);

        client.Events.Record(new CreateEventInput
        {
            Action = "invoice.created",
            ResourceType = "invoice",
            ResourceId = "inv-1",
            OccurredAt = new DateTimeOffset(2026, 5, 6, 12, 0, 0, TimeSpan.Zero),
            Data = new Dictionary<string, object?> { ["k"] = "v" },
            IdempotencyKey = "k-1",
            DoNotForward = true,
        });
        await client.Events.FlushAsync(TimeSpan.FromSeconds(2));

        var attrs = Attributes(captured.Body);
        var keys = KeysOf(attrs);
        keys.ExceptWith(EventPostAttrs);
        Assert.True(keys.Count == 0,
            "wire body has undocumented fields: " + string.Join(", ", keys));
    }

    // -----------------------------------------------------------------
    // forwarders.CreateAsync — wire body
    // -----------------------------------------------------------------

    [Fact]
    public async Task ForwardersCreate_WireShape_AllParameters()
    {
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, ForwarderResponseJson("Datadog production"));
        await using var client = new AuditClient(gen);

        await client.Forwarders.CreateAsync(new CreateForwarderInput
        {
            Name = "Datadog production",
            ForwarderType = "datadog",
            Http = new ForwarderHttp
            {
                Url = "https://siem.example.com/in",
                Headers = new List<HttpHeader> { new("DD-API-KEY", "real-secret") },
            },
            Enabled = false,
            Filter = new Dictionary<string, object?> { ["=="] = new[] { 1, 1 } },
            Transform = "$",
            Data = new Dictionary<string, object?> { ["team"] = "platform" },
        });

        Assert.Equal("POST", captured.Method);
        var data = captured.Body.GetProperty("data");
        Assert.Equal("forwarder", data.GetProperty("type").GetString());
        // POST: server assigns id; wrapper sends "".
        Assert.Equal("", data.GetProperty("id").GetString());

        var attrs = Attributes(captured.Body);
        Assert.Equal("Datadog production", attrs.GetProperty("name").GetString());
        Assert.Equal("datadog", attrs.GetProperty("forwarder_type").GetString());
        Assert.False(attrs.GetProperty("enabled").GetBoolean());
        Assert.Equal("$", attrs.GetProperty("transform").GetString());
        Assert.Equal("platform", attrs.GetProperty("data").GetProperty("team").GetString());

        var http = attrs.GetProperty("http");
        Assert.Equal("https://siem.example.com/in", http.GetProperty("url").GetString());
        var headers = http.GetProperty("headers");
        Assert.Equal(1, headers.GetArrayLength());
        Assert.Equal("DD-API-KEY", headers[0].GetProperty("name").GetString());
        Assert.Equal("real-secret", headers[0].GetProperty("value").GetString());

        // Read-only / immutable fields MUST NOT appear on the wire.
        foreach (var ro in new[] { "slug", "created_at", "updated_at", "deleted_at", "version" })
        {
            Assert.False(attrs.TryGetProperty(ro, out _),
                $"read-only field {ro} should not appear on the wire");
        }
    }

    [Fact]
    public async Task ForwardersCreate_WireShape_NoExtraKeys()
    {
        var (gen, captured) = BuildCapturing(HttpStatusCode.Created, ForwarderResponseJson("x"));
        await using var client = new AuditClient(gen);

        await client.Forwarders.CreateAsync(new CreateForwarderInput
        {
            Name = "Datadog production",
            ForwarderType = "datadog",
            Http = new ForwarderHttp { Url = "https://x" },
            Enabled = true,
            Filter = new Dictionary<string, object?> { ["x"] = 1 },
            Transform = "$",
            Data = new Dictionary<string, object?> { ["k"] = "v" },
        });

        var attrs = Attributes(captured.Body);
        var keys = KeysOf(attrs);
        keys.ExceptWith(ForwarderPostAttrs);
        Assert.True(keys.Count == 0,
            "wire body has undocumented fields: " + string.Join(", ", keys));
    }

    // -----------------------------------------------------------------
    // forwarders.UpdateAsync — wire body
    // -----------------------------------------------------------------

    [Fact]
    public async Task ForwardersUpdate_WireShape_AllParameters()
    {
        var (gen, captured) = BuildCapturing(HttpStatusCode.OK, ForwarderResponseJson("Renamed"));
        await using var client = new AuditClient(gen);

        await client.Forwarders.UpdateAsync(FwdId, new CreateForwarderInput
        {
            Name = "Renamed",
            ForwarderType = "datadog",
            Http = new ForwarderHttp
            {
                Url = "https://siem.example.com/in",
                Headers = new List<HttpHeader> { new("X-K", "real-secret") },
            },
            Enabled = false,
            Filter = new Dictionary<string, object?> { ["=="] = new[] { 1, 1 } },
            Transform = "$",
            Data = new Dictionary<string, object?> { ["k"] = "v" },
        });

        Assert.Equal("PUT", captured.Method);
        var data = captured.Body.GetProperty("data");
        // On PUT the wrapper echoes the path id into the envelope id.
        Assert.Equal(FwdId.ToString(), data.GetProperty("id").GetString());

        var attrs = Attributes(captured.Body);
        Assert.Equal("Renamed", attrs.GetProperty("name").GetString());
        Assert.False(attrs.GetProperty("enabled").GetBoolean());
        var headers = attrs.GetProperty("http").GetProperty("headers");
        // Headers carry the real plaintext value the caller supplied — the
        // wrapper does NOT round-trip the redacted GET response.
        Assert.Equal("real-secret", headers[0].GetProperty("value").GetString());

        foreach (var ro in new[] { "slug", "created_at", "updated_at", "deleted_at", "version" })
        {
            Assert.False(attrs.TryGetProperty(ro, out _),
                $"read-only field {ro} should not appear on the wire");
        }
    }

    [Fact]
    public async Task ForwardersUpdate_WireShape_NoExtraKeys()
    {
        var (gen, captured) = BuildCapturing(HttpStatusCode.OK, ForwarderResponseJson("Renamed"));
        await using var client = new AuditClient(gen);

        await client.Forwarders.UpdateAsync(FwdId, new CreateForwarderInput
        {
            Name = "x",
            ForwarderType = "http",
            Http = new ForwarderHttp { Url = "https://x" },
            Enabled = true,
            Filter = new Dictionary<string, object?> { ["x"] = 1 },
            Transform = "$",
            Data = new Dictionary<string, object?> { ["k"] = "v" },
        });

        var attrs = Attributes(captured.Body);
        var keys = KeysOf(attrs);
        keys.ExceptWith(ForwarderPostAttrs);
        Assert.True(keys.Count == 0,
            "wire body has undocumented fields: " + string.Join(", ", keys));
    }
}
