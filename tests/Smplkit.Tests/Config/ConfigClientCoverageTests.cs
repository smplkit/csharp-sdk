using System.Net;
using System.Text;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Additional ConfigClient tests for 100% code coverage.
/// Targets less common error paths and edge cases.
/// </summary>
public class ConfigClientCoverageTests
{
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

    private static string SingleConfigListJson(
        string id = "11111111-1111-1111-1111-111111111111",
        string key = "my_key",
        string name = "My Config",
        string? parent = null,
        string? description = null,
        string valuesJson = "{}",
        string environmentsJson = "{}")
    {
        var parentStr = parent is null ? "null" : $"\"{parent}\"";
        var descStr = description is null ? "null" : $"\"{description}\"";
        return $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "config",
                    "attributes": {
                        "key": "{{key}}",
                        "name": "{{name}}",
                        "description": {{descStr}},
                        "parent": {{parentStr}},
                        "items": {{valuesJson}},
                        "environments": {{environmentsJson}},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;
    }

    private static string SingleConfigJson(
        string id = "11111111-1111-1111-1111-111111111111",
        string key = "my_key",
        string name = "My Config",
        string? parent = null,
        string? description = null,
        string valuesJson = "{}",
        string environmentsJson = "{}")
    {
        var parentStr = parent is null ? "null" : $"\"{parent}\"";
        var descStr = description is null ? "null" : $"\"{description}\"";
        return $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "config",
                "attributes": {
                    "key": "{{key}}",
                    "name": "{{name}}",
                    "description": {{descStr}},
                    "parent": {{parentStr}},
                    "items": {{valuesJson}},
                    "environments": {{environmentsJson}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
    }

    // ------------------------------------------------------------------
    // DeleteAsync — 409 Conflict through ConfigClient
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_Conflict_ThrowsSmplConflictException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigListJson()));
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Has children"}]}""",
                HttpStatusCode.Conflict));
        });

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.DeleteAsync("my_key"));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("Has children", ex.ResponseBody!);
    }

    // ------------------------------------------------------------------
    // SaveAsync (update) — 409 Conflict
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_409_ThrowsSmplConflictException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigListJson()));
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Conflict"}]}""",
                HttpStatusCode.Conflict));
        });

        var config = await client.Config.GetAsync("my_key");
        config.Name = "Updated";

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => config.SaveAsync());
        Assert.Equal(409, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // SaveAsync (create) — 409 Conflict
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_409_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Already exists"}]}""",
                HttpStatusCode.Conflict)));

        var config = client.Config.New("test_key", "Test");
        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => config.SaveAsync());
        Assert.Equal(409, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // GetAsync — non-null data with all populated fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_AllFieldsPopulated_MapsCorrectly()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigListJson(
                id: "11111111-1111-1111-1111-111111111111",
                key: "svc_key",
                name: "Service",
                description: "A description",
                parent: "parent-id",
                valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}""",
                environmentsJson: """{"production": {"timeout": {"value": 60}}}"""))));

        var config = await client.Config.GetAsync("svc_key");

        Assert.Equal("11111111-1111-1111-1111-111111111111", config.Id);
        Assert.Equal("svc_key", config.Key);
        Assert.Equal("Service", config.Name);
        Assert.Equal("A description", config.Description);
        Assert.Equal("parent-id", config.Parent);
        Assert.NotNull(config.CreatedAt);
        Assert.NotNull(config.UpdatedAt);
    }

    // ------------------------------------------------------------------
    // GetAsync — URL uses filter[key] query param
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_VerifyUrlFormat()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigListJson(key: "test_key"))));

        await client.Config.GetAsync("test_key");

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        Assert.Contains("filter%5Bkey%5D=test_key", url);
    }

    // ------------------------------------------------------------------
    // ListAsync — verifies URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_CorrectUrl()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": []}""")));

        await client.Config.ListAsync();

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        Assert.EndsWith("/api/v1/configs", url);
    }

    // ------------------------------------------------------------------
    // DeleteAsync — verifies correct UUID used for delete
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_CorrectUrl()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigListJson(
                    id: "12312312-1231-1231-1231-123123123123",
                    key: "target_key")));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });

        await client.Config.DeleteAsync("target_key");

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        Assert.Contains("/api/v1/configs/12312312-1231-1231-1231-123123123123", url);
    }

    // ------------------------------------------------------------------
    // SaveAsync (create) — with null optional fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_NullOptionalFields_SerializesCorrectly()
    {
        string? postBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                postBody = await req.Content!.ReadAsStringAsync();
            }
            return JsonResponse(SingleConfigJson(), HttpStatusCode.Created);
        });

        var config = client.Config.New("minimal");
        await config.SaveAsync();

        Assert.NotNull(postBody);
        Assert.Contains("minimal", postBody);
    }

    // ------------------------------------------------------------------
    // SaveAsync (create) — null attributes in response
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Create_NullAttributesInResponse_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"data": {"id": "abcabc00-abc0-abc0-abc0-abcabc000000", "type": "config", "attributes": null}}""",
                HttpStatusCode.Created)));

        var config = client.Config.New("test_key", "Test");
        await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
    }

    // ------------------------------------------------------------------
    // SaveAsync (update) — null attributes in response
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_NullAttributesInResponse_ThrowsSmplValidationException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigListJson()));
            return Task.FromResult(JsonResponse(
                """{"data": {"id": "abcabc00-abc0-abc0-abc0-abcabc000000", "type": "config", "attributes": null}}"""));
        });

        var config = await client.Config.GetAsync("my_key");
        config.Name = "Updated";

        await Assert.ThrowsAsync<SmplValidationException>(
            () => config.SaveAsync());
    }

    // ------------------------------------------------------------------
    // SaveAsync (update) — sets content type
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_SetsJsonApiContentType()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
                return Task.FromResult(JsonResponse(SingleConfigListJson()));
            return Task.FromResult(JsonResponse(SingleConfigJson()));
        });

        var config = await client.Config.GetAsync("my_key");
        config.Name = "Updated";
        await config.SaveAsync();

        Assert.NotNull(handler.LastRequest);
        var contentType = handler.LastRequest.Content!.Headers.ContentType!.MediaType;
        Assert.Equal("application/json", contentType);
    }

    // ------------------------------------------------------------------
    // Config.ToString
    // ------------------------------------------------------------------

    [Fact]
    public void Config_ToString_ReturnsFormattedString()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), httpClient);

        var config = client.Config.New("my_key", "My Config");

        Assert.Equal("Config(Key=my_key, Name=My Config)", config.ToString());
    }

    // ------------------------------------------------------------------
    // Items mutation and SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Items_Mutation_ThenSaveAsync_IncludesItemsInBody()
    {
        string? putBody = null;
        int requestCount = 0;
        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
                return JsonResponse(SingleConfigListJson(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            if (req.Method == HttpMethod.Put)
                putBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleConfigJson());
        });

        var config = await client.Config.GetAsync("my_key");
        config.Items["new_key"] = "new_value";

        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("new_key", putBody);
        Assert.Contains("new_value", putBody);
    }

    // ------------------------------------------------------------------
    // Environments mutation and SaveAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Environments_Mutation_ThenSaveAsync_IncludesEnvsInBody()
    {
        string? putBody = null;
        int requestCount = 0;
        var (client, _) = CreateClient(async req =>
        {
            requestCount++;
            if (requestCount == 1)
                return JsonResponse(SingleConfigListJson());
            if (req.Method == HttpMethod.Put)
                putBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleConfigJson());
        });

        var config = await client.Config.GetAsync("my_key");
        config.Environments["production"] = new Dictionary<string, object?> { ["timeout"] = 60 };

        await config.SaveAsync();

        Assert.NotNull(putBody);
        Assert.Contains("production", putBody);
    }
}
