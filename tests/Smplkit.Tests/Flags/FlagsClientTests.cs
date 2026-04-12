using System.Net;
using System.Text;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

public class FlagsClientTests
{
    private const string FlagSlug = "my-flag";
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
        string id = FlagSlug,
        string name = FlagName,
        string type = "BOOLEAN",
        string defaultVal = "false") =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "flag",
                "attributes": {
                    "id": "{{id}}",
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
                    "id": "{{FlagSlug}}",
                    "type": "flag",
                    "attributes": {
                        "id": "{{FlagSlug}}",
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
                    "id": "another-flag",
                    "type": "flag",
                    "attributes": {
                        "id": "another-flag",
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

    /// <summary>Single resource response for GetAsync(id).</summary>
    private static string SingleFlagForGetJson(
        string id = FlagSlug,
        string name = FlagName) =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "flag",
                "attributes": {
                    "id": "{{id}}",
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

        var flag = client.Flags.Management.NewBooleanFlag(FlagSlug, false, name: FlagName, description: "Test flag");
        await flag.SaveAsync();

        Assert.Equal(FlagSlug, flag.Id);
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

        var flag = client.Flags.Management.NewBooleanFlag(FlagSlug, false, name: FlagName);
        await flag.SaveAsync();

        Assert.NotNull(capturedBody);
        Assert.Contains("True", capturedBody);
        Assert.Contains("False", capturedBody);
    }

    // ---------------------------------------------------------------
    // GetAsync (by id — direct GET)
    // ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ById_ReturnsFlag()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleFlagForGetJson())));

        var flag = await client.Flags.Management.GetAsync(FlagSlug);

        Assert.Equal(FlagSlug, flag.Id);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("/api/v1/flags", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);
    }

    [Fact]
    public async Task GetAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"errors":[{"status":"404","detail":"Not found"}]}""",
                    Encoding.UTF8, "application/vnd.api+json"),
            }));

        await Assert.ThrowsAsync<SmplNotFoundException>(() =>
            client.Flags.Management.GetAsync("nonexistent-id"));
    }

    // ---------------------------------------------------------------
    // ListAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ReturnsFlagList()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flags = await client.Flags.Management.ListAsync();

        Assert.Equal(2, flags.Count);
        Assert.Equal(FlagSlug, flags[0].Id);
        Assert.Equal("another-flag", flags[1].Id);
        Assert.Contains("/api/v1/flags", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ListAsync_EmptyResponse_ReturnsEmptyList()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": []}""")));

        var flags = await client.Flags.Management.ListAsync();

        Assert.Empty(flags);
    }

    // ---------------------------------------------------------------
    // DeleteAsync (by id — directly calls DELETE)
    // ---------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_ById_CallsCorrectUrl()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Flags.Management.DeleteAsync(FlagSlug);

        Assert.Single(handler.Requests);
        var deleteReq = handler.Requests[0];
        Assert.Equal(HttpMethod.Delete, deleteReq.Method);
        Assert.Contains($"/api/v1/flags/{FlagSlug}", deleteReq.RequestUri!.ToString());
    }
}
