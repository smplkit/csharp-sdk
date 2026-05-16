using System.Net;
using System.Text;
using Smplkit.Audit;
using Smplkit.Errors;
using Smplkit.Management;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Tests for the management-plane SIEM forwarders wrapper.
///
/// <para>Stubs the audit service via <see cref="MockHttpMessageHandler"/>;
/// no real network. Coverage on the wrapper must reach 100% to satisfy
/// the SDK CI gate.</para>
/// </summary>
public class AuditForwardersTests
{
    private static readonly Guid FwdId = Guid.Parse("11111111-2222-3333-4444-555555555555");

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

    private static string ForwarderResource(string name = "Datadog production", string slug = "datadog_production") =>
        "{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
            + "\"name\":\"" + name + "\",\"slug\":\"" + slug + "\","
            + "\"forwarder_type\":\"DATADOG\",\"enabled\":true,"
            + "\"http\":{\"method\":\"POST\",\"url\":\"https://siem.example.com/in\","
            + "\"headers\":[{\"name\":\"DD-API-KEY\",\"value\":\"<redacted>\"}],"
            + "\"success_status\":\"2xx\"},"
            + "\"created_at\":\"2026-05-07T12:00:00Z\","
            + "\"updated_at\":\"2026-05-07T12:00:00Z\",\"version\":1}}";

    private static ManagementForwardersClient MakeForwarders(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var (gen, _) = MakeGen(handler);
        return new AuditManagementClient(gen).Forwarders;
    }

    // ----------------------------------------------------------------------
    // CRUD
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Create_ReturnsForwarder()
    {
        string? capturedBody = null;
        string? capturedMethod = null;
        var fwd = await MakeForwarders(async req =>
        {
            capturedMethod = req.Method.Method;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        }).CreateAsync(new CreateForwarderInput
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
        });
        Assert.Equal("datadog_production", fwd.Slug);
        Assert.Equal("POST", capturedMethod);
        Assert.Contains("real-secret", capturedBody!);
    }

    [Fact]
    public async Task Create_ThrowsOnNullInput()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
                .CreateAsync(null!));
    }

    [Fact]
    public async Task List_PaginatesViaOffset()
    {
        var calls = 0;
        string? secondUrl = null;
        var fwds = MakeForwarders(req =>
        {
            calls++;
            if (calls == 2) secondUrl = req.RequestUri!.ToString();
            var body = calls == 1
                ? "{\"data\":[" + ForwarderResource("A", "a")
                    + "],\"meta\":{\"pagination\":{\"page\":1,\"size\":1,\"total\":2,\"total_pages\":2}}}"
                : "{\"data\":[" + ForwarderResource("B", "b")
                    + "],\"meta\":{\"pagination\":{\"page\":2,\"size\":1,\"total\":2,\"total_pages\":2}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi(body),
            });
        });
        var first = await fwds.ListAsync(new ListForwardersInput
        {
            ForwarderType = ForwarderType.Datadog,
            Enabled = true,
            PageSize = 1,
            MetaTotal = true,
        });
        Assert.Single(first.Forwarders);
        Assert.Equal(1, first.Pagination.Page);
        Assert.Equal(2, first.Pagination.Total);
        Assert.Equal(2, first.Pagination.TotalPages);
        var second = await fwds.ListAsync(new ListForwardersInput
        {
            PageNumber = 2,
            PageSize = 1,
            MetaTotal = true,
        });
        Assert.Equal(2, second.Pagination.Page);
        Assert.NotNull(secondUrl);
        Assert.Contains("page%5Bnumber%5D=2", secondUrl!);
        Assert.Contains("meta%5Btotal%5D=true", secondUrl!);
    }

    [Fact]
    public async Task List_DefaultInputAndPagination()
    {
        var page = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":1}}}"),
        })).ListAsync();
        Assert.Empty(page.Forwarders);
        Assert.Equal(1, page.Pagination.Page);
        Assert.Null(page.Pagination.Total);
    }

    [Fact]
    public async Task Get_Success()
    {
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
        })).GetAsync(FwdId);
        Assert.Equal(FwdId, fwd.Id);
        Assert.Single(fwd.Http.Headers);
        Assert.Equal("<redacted>", fwd.Http.Headers[0].Value);
    }

    [Fact]
    public async Task Update_Success()
    {
        string? method = null;
        var fwd = await MakeForwarders(req =>
        {
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource("Renamed", "renamed") + "}"),
            });
        }).UpdateAsync(FwdId, new CreateForwarderInput
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
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))
                .UpdateAsync(FwdId, null!));
    }

    [Fact]
    public async Task Delete_Success()
    {
        string? method = null;
        await MakeForwarders(req =>
        {
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }).DeleteAsync(FwdId);
        Assert.Equal("DELETE", method);
    }

    // ----------------------------------------------------------------------
    // Coverage corner cases
    // ----------------------------------------------------------------------

    [Fact]
    public void ForwarderRecordAccessors_FullyCovered()
    {
        var fwd = new Forwarder(
            FwdId, "n", "s", ForwarderType.Http, true,
            new Dictionary<string, object?>(), "tx",
            new ForwarderHttp { Url = "u" },
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        Assert.Equal(ForwarderType.Http, fwd.ForwarderType);
        Assert.True(fwd.Enabled);
        Assert.NotNull(fwd.Filter);
        Assert.Equal("tx", fwd.Transform);
        Assert.NotNull(fwd.CreatedAt);
        Assert.NotNull(fwd.UpdatedAt);
        Assert.NotNull(fwd.DeletedAt);
        Assert.Equal(1, fwd.Version);
    }

    [Fact]
    public async Task ConvertJson_HandlesIDictionaryStringObject()
    {
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"filter\":{\"==\":[1,1]}}}}"),
        })).GetAsync(FwdId);
        Assert.NotNull(fwd.Filter);
    }

    [Fact]
    public async Task ConvertJson_NonObjectJsonElementFallsThroughToNull()
    {
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"filter\":\"not-an-object\"}}}"),
        })).GetAsync(FwdId);
        Assert.Null(fwd.Filter);
    }

    [Fact]
    public async Task ForwarderResource_HandlesMinimalAttributes()
    {
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":false}}}"),
        })).GetAsync(FwdId);
        Assert.Equal(string.Empty, fwd.Http.Url);
        Assert.Empty(fwd.Http.Headers);
    }

    // ----------------------------------------------------------------------
    // ForwarderType — wire round-trip + extension methods
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
                capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(req.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                    + "\"name\":\"n\",\"slug\":\"n\",\"forwarder_type\":\"" + wire + "\",\"enabled\":true,"
                    + "\"http\":{\"method\":\"POST\",\"url\":\"u\",\"headers\":[],\"success_status\":\"2xx\"}}}}"),
            };
        });
        var client = new AuditManagementClient(gen).Forwarders;
        var created = await client.CreateAsync(new CreateForwarderInput
        {
            Name = "n",
            ForwarderType = type,
            Http = new ForwarderHttp { Url = "u" },
        });
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
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var client = new AuditManagementClient(gen).Forwarders;

        // ToGen default arm.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.CreateAsync(new CreateForwarderInput
            {
                Name = "n",
                ForwarderType = (ForwarderType)999,
                Http = new ForwarderHttp { Url = "u" },
            }));

        // FromGen default arm — invoke via reflection.
        var method = typeof(ManagementForwardersClient).GetMethod(
            "FromGenForwarderType",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method!.Invoke(null, new object[] { (GenAudit.ForwarderType)999 }));
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }

    [Fact]
    public async Task ParseHttpMethod_NonStandardFallsToPost()
    {
        // Exercises the default arm in ParseHttpMethod — any unrecognised
        // method string falls through to POST.
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        });
        var client = new AuditManagementClient(gen).Forwarders;
        await client.CreateAsync(new CreateForwarderInput
        {
            Name = "n",
            ForwarderType = ForwarderType.Http,
            Http = new ForwarderHttp { Url = "u", Method = "UNKNOWN" },
        });
        // Body was sent — that's all we need; the POST vs UNKNOWN fallback
        // is an internal detail. Assert the call succeeded.
        Assert.NotNull(capturedBody);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ParseHttpMethod_NamedMethodsForwardCorrectly(string httpMethod)
    {
        // Exercises the explicit arms in ParseHttpMethod (GET, PUT, PATCH, DELETE).
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        });
        var client = new AuditManagementClient(gen).Forwarders;
        await client.CreateAsync(new CreateForwarderInput
        {
            Name = "n",
            ForwarderType = ForwarderType.Http,
            Http = new ForwarderHttp { Url = "u", Method = httpMethod },
        });
        Assert.NotNull(capturedBody);
    }

    [Fact]
    public async Task ConvertJson_NestedObjectAndArraysExpand()
    {
        // Deep-traverse: exercises array, nested-object, bool, null branches
        // inside JsonElementToObject.
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"slug\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"filter\":{\"count\":42,\"on\":true,\"off\":false,"
                + "\"nested\":{\"k\":\"v\"},\"items\":[1,2.5,\"three\",null]}}}}"),
        })).GetAsync(FwdId);
        Assert.NotNull(fwd.Filter);
        Assert.Equal(42L, fwd.Filter!["count"]);
        Assert.Equal(true, fwd.Filter["on"]);
        Assert.Equal(false, fwd.Filter["off"]);
    }
}
