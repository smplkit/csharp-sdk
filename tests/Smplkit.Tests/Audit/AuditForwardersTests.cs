using System.Net;
using System.Text;
using Smplkit.Audit;
using Smplkit.Management;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;
using HttpMethod = Smplkit.Audit.HttpMethod;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Tests for the management-plane SIEM forwarders wrapper.
///
/// <para>Stubs the audit service via <see cref="MockHttpMessageHandler"/>;
/// no real network. Coverage on the wrapper must reach 100% to satisfy
/// the SDK CI gate. Exercises the active-record API:
/// <c>mgmt.Audit.Forwarders.New(...)</c> → mutate → <c>SaveAsync</c> /
/// <c>DeleteAsync</c>.</para>
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

    private static string ForwarderResource(string name = "Datadog production") =>
        "{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
            + "\"name\":\"" + name + "\","
            + "\"forwarder_type\":\"DATADOG\",\"enabled\":true,"
            + "\"configuration\":{\"method\":\"POST\",\"url\":\"https://siem.example.com/in\","
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
    // Active record — New + SaveAsync (create path)
    // ----------------------------------------------------------------------

    [Fact]
    public void New_ReturnsUnsavedForwarder_NoNetwork()
    {
        var calls = 0;
        var fwds = MakeForwarders(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var fwd = fwds.New(
            name: "n",
            forwarderType: ForwarderType.Http,
            configuration: new HttpConfiguration { Url = "u" });
        Assert.Null(fwd.Id);
        Assert.Null(fwd.CreatedAt);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SaveAsync_OnNewInstance_PostsAndApplies()
    {
        string? capturedBody = null;
        string? capturedMethod = null;
        var fwds = MakeForwarders(async req =>
        {
            capturedMethod = req.Method.Method;
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        });
        var fwd = fwds.New(
            name: "Datadog production",
            forwarderType: ForwarderType.Datadog,
            configuration: new HttpConfiguration
            {
                Url = "https://siem.example.com/in",
                Headers = new List<HttpHeader> { new("DD-API-KEY", "real-secret") },
            },
            filter: new Dictionary<string, object?> { ["=="] = new[] { 1, 1 } },
            transform: "$",
            transformType: TransformType.Jsonata);
        await fwd.SaveAsync();
        // POST verb, wrapper writes `configuration` (not `http`).
        Assert.Equal("POST", capturedMethod);
        Assert.Contains("\"configuration\":", capturedBody);
        Assert.DoesNotContain("\"http\":", capturedBody);
        // Real header value reaches the wire (redacted only on reads).
        Assert.Contains("real-secret", capturedBody!);
        // Transform forces transform_type=JSONATA per spec.
        Assert.Contains("\"transform_type\":\"JSONATA\"", capturedBody);
        // Server-assigned fields applied to the instance.
        Assert.Equal(FwdId, fwd.Id);
        Assert.NotNull(fwd.CreatedAt);
        Assert.Equal(1, fwd.Version);
    }

    [Fact]
    public async Task SaveAsync_OnExistingInstance_PutsAndApplies()
    {
        string? capturedMethod = null;
        var fwds = MakeForwarders(req =>
        {
            capturedMethod = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource("Renamed") + "}"),
            });
        });
        // GET first to obtain a saved (CreatedAt-set) instance.
        var fwd = await fwds.GetAsync(FwdId);
        fwd.Name = "Renamed";
        fwd.Enabled = false;
        await fwd.SaveAsync();
        Assert.Equal("PUT", capturedMethod);
        Assert.Equal("Renamed", fwd.Name);
    }

    [Fact]
    public async Task SaveAsync_WithoutClient_Throws()
    {
        var fwd = new ForwarderTestHelper().BuildClientlessForwarder();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fwd.SaveAsync());
    }

    [Fact]
    public async Task DeleteAsync_OnInstance_IssuesDelete()
    {
        string? method = null;
        var fwds = MakeForwarders(req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
                });
            method = req.Method.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        var fwd = await fwds.GetAsync(FwdId);
        await fwd.DeleteAsync();
        Assert.Equal("DELETE", method);
    }

    [Fact]
    public async Task DeleteAsync_WithoutClient_Throws()
    {
        var fwd = new ForwarderTestHelper().BuildClientlessForwarder();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fwd.DeleteAsync());
    }

    [Fact]
    public async Task DeleteAsync_WithoutId_Throws()
    {
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" });
        // Unsaved — has a client but no id.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fwd.DeleteAsync());
    }

    // ----------------------------------------------------------------------
    // List / Get
    // ----------------------------------------------------------------------

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
                ? "{\"data\":[" + ForwarderResource("A")
                    + "],\"meta\":{\"pagination\":{\"page\":1,\"size\":1,\"total\":2,\"total_pages\":2}}}"
                : "{\"data\":[" + ForwarderResource("B")
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
    public async Task Get_Success_ReturnsClientBoundInstance()
    {
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
        })).GetAsync(FwdId);
        Assert.Equal(FwdId, fwd.Id);
        Assert.Single(fwd.Configuration.Headers);
        Assert.Equal("<redacted>", fwd.Configuration.Headers[0].Value);
    }

    [Fact]
    public async Task Delete_ById_Success()
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
    // ToString + property coverage
    // ----------------------------------------------------------------------

    [Fact]
    public void Forwarder_ToString_IncludesIdNameEnabled()
    {
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" });
        var s = fwd.ToString();
        Assert.Contains("Name=n", s);
        Assert.Contains("Enabled=True", s);
    }

    // ----------------------------------------------------------------------
    // Wire format — minimal payload (no configuration, no version, no headers)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ConvertJson_HandlesIDictionaryStringObject()
    {
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
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
                + "\"name\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
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
                + "\"name\":\"x\",\"forwarder_type\":\"http\",\"enabled\":false}}}"),
        })).GetAsync(FwdId);
        Assert.Equal(string.Empty, fwd.Configuration.Url);
        Assert.Empty(fwd.Configuration.Headers);
    }

    // ----------------------------------------------------------------------
    // ForwarderType — wire round-trip + extension methods
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(ForwarderType.Http, "http")]
    [InlineData(ForwarderType.Datadog, "datadog")]
    [InlineData(ForwarderType.SplunkHec, "splunk_hec")]
    [InlineData(ForwarderType.SumoLogic, "sumo_logic")]
    [InlineData(ForwarderType.NewRelic, "new_relic")]
    [InlineData(ForwarderType.Honeycomb, "honeycomb")]
    [InlineData(ForwarderType.Elastic, "elastic")]
    public async Task ForwarderType_RoundTripsThroughCreateAndGet(ForwarderType type, string wire)
    {
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            if (req.Method == System.Net.Http.HttpMethod.Post)
                capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(req.Method == System.Net.Http.HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                    + "\"name\":\"n\",\"forwarder_type\":\"" + wire + "\",\"enabled\":true,"
                    + "\"configuration\":{\"method\":\"POST\",\"url\":\"u\",\"headers\":[],\"success_status\":\"2xx\"}}}}"),
            };
        });
        var fwds = new AuditManagementClient(gen).Forwarders;
        var fwd = fwds.New("n", type, new HttpConfiguration { Url = "u" });
        await fwd.SaveAsync();
        Assert.NotNull(capturedBody);
        Assert.Equal(type, fwd.ForwarderType);
    }

    [Fact]
    public void ForwarderTypeExtensions_ToWireValue_RoundTrips()
    {
        Assert.Equal("http", ForwarderType.Http.ToWireValue());
        Assert.Equal("splunk_hec", ForwarderType.SplunkHec.ToWireValue());
        Assert.Equal("elastic", ForwarderType.Elastic.ToWireValue());
    }

    [Theory]
    [InlineData("http", ForwarderType.Http)]
    [InlineData("datadog", ForwarderType.Datadog)]
    [InlineData("splunk_hec", ForwarderType.SplunkHec)]
    [InlineData("sumo_logic", ForwarderType.SumoLogic)]
    [InlineData("new_relic", ForwarderType.NewRelic)]
    [InlineData("honeycomb", ForwarderType.Honeycomb)]
    [InlineData("elastic", ForwarderType.Elastic)]
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
        var fwds = new AuditManagementClient(gen).Forwarders;

        // ToGen default arm — invoked when the customer passes an out-of-range enum.
        var fwd = fwds.New("n", (ForwarderType)999, new HttpConfiguration { Url = "u" });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fwd.SaveAsync());

        // FromGen default arm — invoke via reflection.
        var method = typeof(ManagementForwardersClient).GetMethod(
            "FromGenForwarderType",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => method!.Invoke(null, new object[] { (GenAudit.ForwarderType)999 }));
        Assert.IsType<ArgumentOutOfRangeException>(ex.InnerException);
    }

    // ----------------------------------------------------------------------
    // HttpMethod — wire round-trip
    // ----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpMethod.Get, "GET")]
    [InlineData(HttpMethod.Post, "POST")]
    [InlineData(HttpMethod.Put, "PUT")]
    [InlineData(HttpMethod.Patch, "PATCH")]
    [InlineData(HttpMethod.Delete, "DELETE")]
    public void HttpMethodExtensions_ToWireValue(HttpMethod method, string wire)
    {
        Assert.Equal(wire, method.ToWireValue());
    }

    [Theory]
    [InlineData("GET", HttpMethod.Get)]
    [InlineData("POST", HttpMethod.Post)]
    [InlineData("PUT", HttpMethod.Put)]
    [InlineData("PATCH", HttpMethod.Patch)]
    [InlineData("DELETE", HttpMethod.Delete)]
    public void HttpMethodExtensions_FromWireValue_Known(string wire, HttpMethod expected)
    {
        Assert.Equal(expected, HttpMethodExtensions.FromWireValue(wire));
    }

    [Fact]
    public void HttpMethodExtensions_FromWireValue_UnknownDefaultsToPost()
    {
        Assert.Equal(HttpMethod.Post, HttpMethodExtensions.FromWireValue("UNKNOWN"));
        Assert.Equal(HttpMethod.Post, HttpMethodExtensions.FromWireValue(null!));
    }

    [Fact]
    public void HttpMethodExtensions_ToWireValue_OutOfRangeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((HttpMethod)999).ToWireValue());
    }

    [Theory]
    [InlineData(HttpMethod.Get, "GET")]
    [InlineData(HttpMethod.Put, "PUT")]
    [InlineData(HttpMethod.Patch, "PATCH")]
    [InlineData(HttpMethod.Delete, "DELETE")]
    public async Task HttpMethod_NonPostForwardsCorrectly(HttpMethod method, string wire)
    {
        // Exercise each ToGenHttpMethod arm via SaveAsync.
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        });
        var fwds = new AuditManagementClient(gen).Forwarders;
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration
        {
            Url = "u",
            Method = method,
        });
        await fwd.SaveAsync();
        Assert.NotNull(capturedBody);
        Assert.Contains($"\"method\":\"{wire}\"", capturedBody!);
    }

    [Fact]
    public async Task HttpMethod_ToGenOutOfRange_Throws()
    {
        var (gen, _) = MakeGen(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwds = new AuditManagementClient(gen).Forwarders;
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration
        {
            Url = "u",
            Method = (HttpMethod)999,
        });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fwd.SaveAsync());
    }

    [Fact]
    public async Task SaveUpdateAsync_NoId_Throws()
    {
        // Drive SaveUpdateAsync directly with an unsaved Forwarder (Id is null) —
        // the guard branch only reachable via reflection because public
        // SaveAsync would route to SaveCreateAsync instead.
        var (gen, _) = MakeGen(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwds = new AuditManagementClient(gen).Forwarders;
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" });
        var method = typeof(ManagementForwardersClient).GetMethod(
            "SaveUpdateAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            var task = (Task)method.Invoke(fwds, new object?[] { fwd, default(CancellationToken) })!;
            await task;
        });
    }

    // ----------------------------------------------------------------------
    // TransformType — wire round-trip
    // ----------------------------------------------------------------------

    [Fact]
    public void TransformTypeExtensions_ToWireValue()
    {
        Assert.Equal("JSONATA", TransformType.Jsonata.ToWireValue());
    }

    [Fact]
    public void TransformTypeExtensions_ToWireValue_OutOfRangeThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((TransformType)999).ToWireValue());
    }

    [Fact]
    public void TransformTypeExtensions_FromWireValue_Known()
    {
        Assert.Equal(TransformType.Jsonata, TransformTypeExtensions.FromWireValue("JSONATA"));
    }

    [Fact]
    public void TransformTypeExtensions_FromWireValue_UnknownThrows()
    {
        Assert.Throws<ArgumentException>(() => TransformTypeExtensions.FromWireValue("OTHER"));
    }

    [Fact]
    public async Task TransformType_PopulatedFromWireResponse()
    {
        // Server returns transform_type=JSONATA → wrapper exposes the typed enum.
        var fwd = await MakeForwarders(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi(
                "{\"data\":{\"id\":\"" + FwdId + "\",\"type\":\"forwarder\",\"attributes\":{"
                + "\"name\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"transform\":\"$\",\"transform_type\":\"JSONATA\"}}}"),
        })).GetAsync(FwdId);
        Assert.Equal(TransformType.Jsonata, fwd.TransformType);
    }

    [Fact]
    public void New_TransformWithoutTransformType_Throws()
    {
        // Pairing rule: transform requires transformType. Enforced at New().
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        Assert.Throws<ArgumentException>(() => fwds.New(
            name: "n",
            forwarderType: ForwarderType.Http,
            configuration: new HttpConfiguration { Url = "u" },
            transform: "$"));
    }

    [Fact]
    public async Task SaveAsync_TransformSetLater_WithoutTransformType_Throws()
    {
        // Pairing rule re-enforced at save time when fields are mutated after New().
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" });
        fwd.Transform = "$";
        await Assert.ThrowsAsync<ArgumentException>(() => fwd.SaveAsync());
    }

    [Fact]
    public void New_TransformTypeWithoutTransform_Throws()
    {
        // Pairing rule (reverse direction): transformType requires transform too.
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = Assert.Throws<ArgumentException>(() => fwds.New(
            name: "n",
            forwarderType: ForwarderType.Http,
            configuration: new HttpConfiguration { Url = "u" },
            transformType: TransformType.Jsonata));
        Assert.Contains("transform is required", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_TransformTypeSetLater_WithoutTransform_Throws()
    {
        // Reverse pairing rule re-enforced at save time.
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" });
        fwd.TransformType = TransformType.Jsonata;
        await Assert.ThrowsAsync<ArgumentException>(() => fwd.SaveAsync());
    }

    [Fact]
    public void New_JsonataWithNonStringTransform_Throws()
    {
        // JSONATA expressions are always strings — a dict or other value is
        // rejected even though the wire field is untyped.
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var ex = Assert.Throws<ArgumentException>(() => fwds.New(
            name: "n",
            forwarderType: ForwarderType.Http,
            configuration: new HttpConfiguration { Url = "u" },
            transform: new Dictionary<string, object?> { ["expr"] = "$" },
            transformType: TransformType.Jsonata));
        Assert.Contains("string", ex.Message);
    }

    [Fact]
    public async Task SaveAsync_JsonataWithNonStringTransform_SetAfterNew_Throws()
    {
        // Same constraint re-enforced at save time.
        var fwds = MakeForwarders(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" });
        fwd.TransformType = TransformType.Jsonata;
        fwd.Transform = 42;
        await Assert.ThrowsAsync<ArgumentException>(() => fwd.SaveAsync());
    }

    [Fact]
    public void ConvertTransform_PassesNonJsonElementValueThrough()
    {
        // Exercises the fallback arm of ConvertTransform — anything that's
        // already typed (not a JsonElement, not null) is returned as-is. In
        // production the wire DTO always yields JsonElement; this arm guards
        // future code paths or hand-constructed DTOs.
        var method = typeof(ManagementForwardersClient).GetMethod(
            "ConvertTransform",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal("plain-string", method.Invoke(null, new object?[] { "plain-string" }));
    }

    [Fact]
    public async Task Description_RoundTrips()
    {
        // Set Description before save, confirm it appears in the wire body.
        string? capturedBody = null;
        var (gen, _) = MakeGen(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = JsonApi("{\"data\":" + ForwarderResource() + "}"),
            };
        });
        var fwds = new AuditManagementClient(gen).Forwarders;
        var fwd = fwds.New("n", ForwarderType.Http, new HttpConfiguration { Url = "u" }, description: "demo");
        await fwd.SaveAsync();
        Assert.Contains("\"description\":\"demo\"", capturedBody!);
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
                + "\"name\":\"x\",\"forwarder_type\":\"http\",\"enabled\":true,"
                + "\"filter\":{\"count\":42,\"on\":true,\"off\":false,"
                + "\"nested\":{\"k\":\"v\"},\"items\":[1,2.5,\"three\",null]}}}}"),
        })).GetAsync(FwdId);
        Assert.NotNull(fwd.Filter);
        Assert.Equal(42L, fwd.Filter!["count"]);
        Assert.Equal(true, fwd.Filter["on"]);
        Assert.Equal(false, fwd.Filter["off"]);
    }

    /// <summary>Helper to construct a <see cref="Forwarder"/> with no bound client,
    /// for testing the guard clauses on SaveAsync / DeleteAsync.</summary>
    private sealed class ForwarderTestHelper
    {
        internal Forwarder BuildClientlessForwarder() =>
            (Forwarder)System.Activator.CreateInstance(
                typeof(Forwarder),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                binder: null,
                args: new object?[]
                {
                    null,  // client
                    "n",   // name
                    ForwarderType.Http,
                    new HttpConfiguration { Url = "u" },
                    true,  // enabled
                    null,  // description
                    null,  // filter
                    null,  // transform
                    null,  // transformType
                    null,  // id
                    null,  // createdAt
                    null,  // updatedAt
                    null,  // deletedAt
                    null,  // version
                },
                culture: null)!;
    }
}
