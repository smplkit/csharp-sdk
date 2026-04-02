using System.Net;
using System.Text;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

public class FlagsClientTests
{
    private const string FlagId = "aaa11111-bbbb-cccc-dddd-eeeeeeee0001";
    private const string FlagKey = "my-flag";
    private const string FlagName = "My Flag";

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

    private static string SingleFlagJson(
        string id = FlagId,
        string key = FlagKey,
        string name = FlagName,
        string type = "BOOLEAN",
        string defaultVal = "false") =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "flag",
                "attributes": {
                    "key": "{{key}}",
                    "name": "{{name}}",
                    "type": "{{type}}",
                    "default": {{defaultVal}},
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Test flag",
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private static string FlagListJson() =>
        $$"""
        {
            "data": [
                {
                    "id": "{{FlagId}}",
                    "type": "flag",
                    "attributes": {
                        "key": "{{FlagKey}}",
                        "name": "{{FlagName}}",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                        "description": "Test flag",
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "aaa11111-bbbb-cccc-dddd-eeeeeeee0002",
                    "type": "flag",
                    "attributes": {
                        "key": "another-flag",
                        "name": "Another Flag",
                        "type": "STRING",
                        "default": "hello",
                        "values": [],
                        "description": null,
                        "environments": {},
                        "created_at": "2024-01-16T10:30:00Z",
                        "updated_at": "2024-01-16T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static string SingleContextTypeJson(
        string id = "ct-001",
        string key = "user",
        string name = "User") =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "context_type",
                "attributes": {
                    "key": "{{key}}",
                    "name": "{{name}}",
                    "attributes": {"plan": "string", "region": "string"}
                }
            }
        }
        """;

    private static string ContextTypeListJson() =>
        """
        {
            "data": [
                {
                    "id": "ct-001",
                    "type": "context_type",
                    "attributes": {
                        "key": "user",
                        "name": "User",
                        "attributes": {"plan": "string"}
                    }
                },
                {
                    "id": "ct-002",
                    "type": "context_type",
                    "attributes": {
                        "key": "account",
                        "name": "Account",
                        "attributes": {}
                    }
                }
            ]
        }
        """;

    private static string ContextListJson() =>
        """
        {
            "data": [
                {"id": "user:user-1", "name": "Alice", "attributes": {"plan": "pro"}},
                {"id": "user:user-2", "name": "Bob", "attributes": {"plan": "free"}}
            ]
        }
        """;

    // ---------------------------------------------------------------
    // CreateAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_ReturnsFlag()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleFlagJson())));

        var flag = await client.Flags.CreateAsync(
            FlagKey, FlagName, FlagType.Boolean, false, description: "Test flag");

        Assert.Equal(FlagId, flag.Id);
        Assert.Equal(FlagKey, flag.Key);
        Assert.Equal(FlagName, flag.Name);
        Assert.Equal("BOOLEAN", flag.Type);
        Assert.Equal("Test flag", flag.Description);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("/api/v1/flags", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task CreateAsync_BooleanAutoGeneratesValues()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleFlagJson());
        });

        await client.Flags.CreateAsync(FlagKey, FlagName, FlagType.Boolean, false);

        Assert.NotNull(capturedBody);
        Assert.Contains("True", capturedBody);
        Assert.Contains("False", capturedBody);
    }

    // ---------------------------------------------------------------
    // GetAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsFlag()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleFlagJson())));

        var flag = await client.Flags.GetAsync(FlagId);

        Assert.Equal(FlagId, flag.Id);
        Assert.Equal(FlagKey, flag.Key);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains($"/api/v1/flags/{FlagId}", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    // ---------------------------------------------------------------
    // ListAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsFlagList()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flags = await client.Flags.ListAsync();

        Assert.Equal(2, flags.Count);
        Assert.Equal(FlagKey, flags[0].Key);
        Assert.Equal("another-flag", flags[1].Key);
        Assert.Contains("/api/v1/flags", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListAsync_EmptyResponse_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": []}""")));

        var flags = await client.Flags.ListAsync();

        Assert.Empty(flags);
    }

    // ---------------------------------------------------------------
    // DeleteAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_CallsCorrectUrl()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Flags.DeleteAsync(FlagId);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains($"/api/v1/flags/{FlagId}", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    // ---------------------------------------------------------------
    // CreateContextTypeAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateContextTypeAsync_ReturnsContextType()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleContextTypeJson())));

        var ct = await client.Flags.CreateContextTypeAsync("user", "User");

        Assert.Equal("ct-001", ct.Id);
        Assert.Equal("user", ct.Key);
        Assert.Equal("User", ct.Name);
        Assert.NotNull(ct.Attributes);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Contains("/api/v1/context_types", handler.LastRequest.RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // UpdateContextTypeAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task UpdateContextTypeAsync_ReturnsUpdatedContextType()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleContextTypeJson())));

        var attrs = new Dictionary<string, object?> { ["plan"] = "string" };
        var ct = await client.Flags.UpdateContextTypeAsync("ct-001", attrs);

        Assert.Equal("ct-001", ct.Id);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Put, handler.LastRequest.Method);
        Assert.Contains("/api/v1/context_types/ct-001", handler.LastRequest.RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // ListContextTypesAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListContextTypesAsync_ReturnsList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(ContextTypeListJson())));

        var types = await client.Flags.ListContextTypesAsync();

        Assert.Equal(2, types.Count);
        Assert.Equal("user", types[0].Key);
        Assert.Equal("account", types[1].Key);
    }

    // ---------------------------------------------------------------
    // DeleteContextTypeAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteContextTypeAsync_CallsCorrectUrl()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Flags.DeleteContextTypeAsync("ct-001");

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/api/v1/context_types/ct-001", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    // ---------------------------------------------------------------
    // ListContextsAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListContextsAsync_WithFilter_ReturnsContexts()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(ContextListJson())));

        var contexts = await client.Flags.ListContextsAsync("user");

        Assert.Equal(2, contexts.Count);
        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.ToString();
        Assert.Contains("context_type", url);
        Assert.Contains("user", url);
    }
}
