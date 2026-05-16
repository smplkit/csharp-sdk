using System.Net;
using System.Text;
using Smplkit.Audit;
using Smplkit.Tests.Helpers;
using Xunit;
using GenAudit = Smplkit.Internal.Generated.Audit;

namespace Smplkit.Tests.Audit;

/// <summary>
/// Tests for <see cref="AuditResourceTypes"/> and <see cref="AuditActions"/>
/// — the read-side distinct-value surfaces added to the runtime audit client.
/// </summary>
public class AuditResourceTypesActionsTests
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
    // Actions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Actions_List_ReturnsPage()
    {
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("""
                {
                  "data": [
                    {"id":"invoice.created","type":"action","attributes":{"action":"invoice.created","created_at":"2026-01-01T00:00:00Z"}},
                    {"id":"user.updated","type":"action","attributes":{"action":"user.updated","created_at":"2026-01-02T00:00:00Z"}}
                  ],
                  "meta":{"pagination":{"page":1,"size":50}}
                }
                """),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Actions.ListAsync();

        Assert.Equal(2, page.Actions.Count);
        Assert.Equal("invoice.created", page.Actions[0].Id);
        Assert.Equal(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), page.Actions[0].CreatedAt);
        Assert.Equal("user.updated", page.Actions[1].Id);
        Assert.Equal(50, page.Pagination.Size);
    }

    [Fact]
    public async Task Actions_List_PassesPaginationAndReturnsTotals()
    {
        string? capturedUrl = null;
        var (gen, _) = MakeGen(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonApi(
                    "{\"data\":[{\"id\":\"invoice.created\",\"type\":\"action\","
                    + "\"attributes\":{\"action\":\"invoice.created\","
                    + "\"created_at\":\"2026-01-01T00:00:00Z\"}}],"
                    + "\"meta\":{\"pagination\":{\"page\":2,\"size\":1,\"total\":3,\"total_pages\":3}}}"),
            });
        });
        await using var client = new AuditClient(gen);
        var page = await client.Actions.ListAsync(new ListActionsInput
        {
            PageNumber = 2,
            PageSize = 1,
            MetaTotal = true,
        });
        Assert.Single(page.Actions);
        Assert.Equal(2, page.Pagination.Page);
        Assert.Equal(3, page.Pagination.Total);
        Assert.Equal(3, page.Pagination.TotalPages);
        Assert.NotNull(capturedUrl);
        Assert.Contains("page%5Bnumber%5D=2", capturedUrl!);
        Assert.Contains("meta%5Btotal%5D=true", capturedUrl!);
    }

    [Fact]
    public async Task Actions_List_FilterResourceTypeAndDefaultInput()
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
        var page = await client.Actions.ListAsync(new ListActionsInput
        {
            FilterResourceType = "invoice",
            PageSize = 10,
        });
        Assert.NotNull(capturedUrl);
        Assert.Contains("invoice", capturedUrl!);
        Assert.Empty(page.Actions);
        Assert.Equal(50, page.Pagination.Size);
    }

    [Fact]
    public void AuditActionRecord_AccessorsCovered()
    {
        var a = new AuditAction("invoice.created", DateTimeOffset.UtcNow);
        Assert.Equal("invoice.created", a.Id);
        Assert.True(a.CreatedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task Actions_List_DefaultInputPath()
    {
        // Exercises the (input ??= new ListActionsInput()) branch when null is passed.
        var (gen, _) = MakeGen(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonApi("{\"data\":[],\"meta\":{\"pagination\":{\"page\":1,\"size\":50}}}"),
        }));
        await using var client = new AuditClient(gen);
        var page = await client.Actions.ListAsync(null);
        Assert.Empty(page.Actions);
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
}
