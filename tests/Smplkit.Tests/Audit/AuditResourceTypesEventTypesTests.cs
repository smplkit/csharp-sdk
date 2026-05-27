using System.Net;
using System.Text;
using Smplkit.Audit;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditResourceTypes"/> and <see cref="AuditEventTypes"/>
/// — the read-side distinct-value surfaces added to the runtime audit client.
/// </summary>
public class AuditResourceTypesEventTypesTests
{
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

    // ------------------------------------------------------------------
    // ResourceTypes
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResourceTypes_List_ReturnsPage()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("""
                {
                  "data": [
                    {"id":"invoice","type":"resource_type","attributes":{"resource_type":"invoice","created_at":"2026-01-01T00:00:00Z"}},
                    {"id":"user","type":"resource_type","attributes":{"resource_type":"user","created_at":"2026-01-02T00:00:00Z"}}
                  ],
                  "meta":{"pagination":{"page":1,"size":50}}
                }
                """),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.ResourceTypes.ListAsync();

        Assert.Equal(2, page.ResourceTypes.Count);
        Assert.Equal("invoice", page.ResourceTypes[0].Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), page.ResourceTypes[0].CreatedAt);
        Assert.Equal("user", page.ResourceTypes[1].Id);
        Assert.Equal(1, page.Pagination.Page);
        Assert.Equal(50, page.Pagination.Size);
        Assert.Null(page.Pagination.Total);
        Assert.Null(page.Pagination.TotalPages);
    }

    [Fact]
    public async Task ResourceTypes_List_PassesPaginationAndReturnsTotals()
    {
        string? capturedUrl = null;
        var (gen, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":[{\"id\":\"invoice\",\"type\":\"resource_type\","
                    + "\"attributes\":{\"resource_type\":\"invoice\","
                    + "\"created_at\":\"2026-01-01T00:00:00Z\"}}],"
                    + "\"meta\":{\"pagination\":{\"page\":2,\"size\":1,\"total\":3,\"total_pages\":3}}}"),
            });
        });
        await using var client = new AuditClient(gen);
        var page = await client.ResourceTypes.ListAsync(new ListResourceTypesInput
        {
            PageNumber = 2,
            PageSize = 1,
            MetaTotal = true,
        });
        Assert.Single(page.ResourceTypes);
        Assert.Equal(2, page.Pagination.Page);
        Assert.Equal(1, page.Pagination.Size);
        Assert.Equal(3, page.Pagination.Total);
        Assert.Equal(3, page.Pagination.TotalPages);
        Assert.NotNull(capturedUrl);
        Assert.Contains("page%5Bnumber%5D=2", capturedUrl!);
        Assert.Contains("page%5Bsize%5D=1", capturedUrl!);
        Assert.Contains("meta%5Btotal%5D=true", capturedUrl!);
    }

    [Fact]
    public async Task ResourceTypes_List_DefaultInputAndEmptyData()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.ResourceTypes.ListAsync();
        Assert.Empty(page.ResourceTypes);
        Assert.Equal(1, page.Pagination.Page);
    }

    [Fact]
    public void ResourceTypeRecord_AccessorsCovered()
    {
        var rt = new ResourceType("invoice", DateTimeOffset.UtcNow);
        Assert.Equal("invoice", rt.Id);
        Assert.True(rt.CreatedAt > DateTimeOffset.MinValue);
    }

    // ------------------------------------------------------------------
    // EventTypes
    // ------------------------------------------------------------------

    [Fact]
    public async Task EventTypes_List_ReturnsPage()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("""
                {
                  "data": [
                    {"id":"invoice.created","type":"event_type","attributes":{"event_type":"invoice.created","created_at":"2026-01-01T00:00:00Z"}},
                    {"id":"user.updated","type":"event_type","attributes":{"event_type":"user.updated","created_at":"2026-01-02T00:00:00Z"}}
                  ],
                  "meta":{"pagination":{"page":1,"size":50}}
                }
                """),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.EventTypes.ListAsync();

        Assert.Equal(2, page.EventTypes.Count);
        Assert.Equal("invoice.created", page.EventTypes[0].Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), page.EventTypes[0].CreatedAt);
        Assert.Equal("user.updated", page.EventTypes[1].Id);
        Assert.Equal(50, page.Pagination.Size);
    }

    [Fact]
    public async Task EventTypes_List_PassesPaginationAndReturnsTotals()
    {
        string? capturedUrl = null;
        var (gen, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":[{\"id\":\"invoice.created\",\"type\":\"event_type\","
                    + "\"attributes\":{\"event_type\":\"invoice.created\","
                    + "\"created_at\":\"2026-01-01T00:00:00Z\"}}],"
                    + "\"meta\":{\"pagination\":{\"page\":2,\"size\":1,\"total\":3,\"total_pages\":3}}}"),
            });
        });
        await using var client = new AuditClient(gen);
        var page = await client.EventTypes.ListAsync(new ListEventTypesInput
        {
            PageNumber = 2,
            PageSize = 1,
            MetaTotal = true,
        });
        Assert.Single(page.EventTypes);
        Assert.Equal(2, page.Pagination.Page);
        Assert.Equal(3, page.Pagination.Total);
        Assert.Equal(3, page.Pagination.TotalPages);
        Assert.NotNull(capturedUrl);
        Assert.Contains("page%5Bnumber%5D=2", capturedUrl!);
        Assert.Contains("meta%5Btotal%5D=true", capturedUrl!);
    }

    [Fact]
    public async Task EventTypes_List_FilterResourceTypeAndDefaultInput()
    {
        string? capturedUrl = null;
        var (gen, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
            });
        });
        await using var client = new AuditClient(gen);
        var page = await client.EventTypes.ListAsync(new ListEventTypesInput
        {
            FilterResourceType = "invoice",
            PageSize = 10,
        });
        Assert.NotNull(capturedUrl);
        Assert.Contains("invoice", capturedUrl!);
        Assert.Empty(page.EventTypes);
        Assert.Equal(50, page.Pagination.Size);
    }

    [Fact]
    public void AuditEventTypeRecord_AccessorsCovered()
    {
        var a = new AuditEventType("invoice.created", DateTimeOffset.UtcNow);
        Assert.Equal("invoice.created", a.Id);
        Assert.True(a.CreatedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task EventTypes_List_DefaultInputPath()
    {
        // Exercises the (input ??= new ListEventTypesInput()) branch when null is passed.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.EventTypes.ListAsync(null);
        Assert.Empty(page.EventTypes);
    }

    [Fact]
    public async Task ResourceTypes_List_DefaultInputPath()
    {
        // Exercises the (input ??= new ListResourceTypesInput()) branch when null is passed.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.ResourceTypes.ListAsync(null);
        Assert.Empty(page.ResourceTypes);
    }

    [Fact]
    public void ExtractPagination_HandlesNullMeta()
    {
        // Defensive path: if the server responds with no meta block at all
        // (or a deserialised null), the helper should produce a zero-valued Pagination.
        var p = AuditResourceTypes.ExtractPagination(null);
        Assert.Equal(0, p.Page);
        Assert.Equal(0, p.Size);
        Assert.Null(p.Total);
        Assert.Null(p.TotalPages);
    }

    // ------------------------------------------------------------------
    // Categories
    // ------------------------------------------------------------------

    [Fact]
    public async Task Categories_List_ReturnsPage()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("""
                {
                  "data": [
                    {"id":"auth","type":"category","attributes":{"category":"auth","created_at":"2026-01-01T00:00:00Z"}},
                    {"id":"billing","type":"category","attributes":{"category":"billing","created_at":"2026-01-02T00:00:00Z"}}
                  ],
                  "meta":{"pagination":{"page":1,"size":50}}
                }
                """),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Categories.ListAsync();

        Assert.Equal(2, page.Categories.Count);
        Assert.Equal("auth", page.Categories[0].Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), page.Categories[0].CreatedAt);
        Assert.Equal("billing", page.Categories[1].Id);
        Assert.Equal(1, page.Pagination.Page);
        Assert.Equal(50, page.Pagination.Size);
    }

    [Fact]
    public async Task Categories_List_PassesPaginationAndReturnsTotals()
    {
        string? capturedUrl = null;
        var (gen, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":[{\"id\":\"auth\",\"type\":\"category\","
                    + "\"attributes\":{\"category\":\"auth\","
                    + "\"created_at\":\"2026-01-01T00:00:00Z\"}}],"
                    + "\"meta\":{\"pagination\":{\"page\":2,\"size\":1,\"total\":3,\"total_pages\":3}}}"),
            });
        });
        await using var client = new AuditClient(gen);
        var page = await client.Categories.ListAsync(new ListCategoriesInput
        {
            PageNumber = 2,
            PageSize = 1,
            MetaTotal = true,
        });
        Assert.Single(page.Categories);
        Assert.Equal(2, page.Pagination.Page);
        Assert.Equal(3, page.Pagination.Total);
        Assert.Contains("page%5Bnumber%5D=2", capturedUrl!);
    }

    [Fact]
    public async Task Categories_List_DefaultInputAndEmptyData()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Categories.ListAsync(null);
        Assert.Empty(page.Categories);
    }
}
