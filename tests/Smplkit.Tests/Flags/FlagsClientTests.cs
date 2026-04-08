using System.Net;
using System.Text;
using Smplkit.Errors;
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

    /// <summary>List response with a single flag matching the given key, used for GetAsync(key).</summary>
    private static string FlagListForGetJson(
        string id = FlagId,
        string key = FlagKey,
        string name = FlagName) =>
        $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "flag",
                    "attributes": {
                        "key": "{{key}}",
                        "name": "{{name}}",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                        "description": "Test flag",
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    // ---------------------------------------------------------------
    // SaveAsync (create — Id is null → POST)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_ReturnsFlag()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleFlagJson(), HttpStatusCode.Created)));

        var flag = client.Flags.NewBooleanFlag(FlagKey, false, name: FlagName, description: "Test flag");
        await flag.SaveAsync();

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
    public async Task SaveAsync_Create_BooleanAutoGeneratesValues()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleFlagJson(), HttpStatusCode.Created);
        });

        var flag = client.Flags.NewBooleanFlag(FlagKey, false, name: FlagName);
        await flag.SaveAsync();

        Assert.NotNull(capturedBody);
        Assert.Contains("True", capturedBody);
        Assert.Contains("False", capturedBody);
    }

    // ---------------------------------------------------------------
    // GetAsync (by key — uses list with filter)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ByKey_ReturnsFlag()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListForGetJson())));

        var flag = await client.Flags.GetAsync(FlagKey);

        Assert.Equal(FlagId, flag.Id);
        Assert.Equal(FlagKey, flag.Key);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/api/v1/flags", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": []}""")));

        await Assert.ThrowsAsync<SmplNotFoundException>(() =>
            client.Flags.GetAsync("nonexistent-key"));
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
    // DeleteAsync (by key — internally calls GetAsync then DELETE by UUID)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ByKey_CallsCorrectUrls()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // First request: list with filter[key] for GetAsync(key)
                return Task.FromResult(JsonResponse(FlagListForGetJson()));
            }
            // Second request: DELETE by UUID
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        await client.Flags.DeleteAsync(FlagKey);

        Assert.True(handler.Requests.Count >= 2);
        // First request is the list/filter
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        // Second request is the DELETE by UUID
        var deleteReq = handler.Requests.Last(r => r.Method == HttpMethod.Delete);
        Assert.Contains($"/api/v1/flags/{FlagId}", deleteReq.RequestUri!.ToString());
    }
}
