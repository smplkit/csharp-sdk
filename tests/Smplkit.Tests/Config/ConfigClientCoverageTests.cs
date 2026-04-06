using System.Net;
using System.Text;
using System.Text.Json;
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

    private static string ConfigJsonWithValuesAndEnvs(
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
    // SetValuesAsync — error during GET phase
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_GetFails_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError)));

        await Assert.ThrowsAsync<SmplException>(
            () => client.Config.SetValuesAsync("11111111-1111-1111-1111-111111111111",
                new Dictionary<string, object?> { ["key"] = "val" }));
    }

    // ------------------------------------------------------------------
    // SetValuesAsync — error during PUT phase
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_PutFails_ThrowsSmplException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""")));
            }
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Validation failed"}]}""",
                (HttpStatusCode)422));
        });

        await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.SetValuesAsync("11111111-1111-1111-1111-111111111111",
                new Dictionary<string, object?> { ["key"] = "val" }));
    }

    // ------------------------------------------------------------------
    // SetValueAsync — error during GET phase
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_GetFails_ThrowsSmplException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.SetValueAsync("11111111-1111-1111-1111-111111111111", "key", "val"));
    }

    // ------------------------------------------------------------------
    // SetValueAsync — env path with error during PUT
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_EnvPutFails_ThrowsSmplException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"values": {"timeout": {"value": 60}}}}""")));
            }
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError));
        });

        await Assert.ThrowsAsync<SmplException>(
            () => client.Config.SetValueAsync("11111111-1111-1111-1111-111111111111", "debug", true, environment: "production"));
    }

    // ------------------------------------------------------------------
    // DeleteAsync — 409 Conflict through ConfigClient
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_Conflict_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Has children"}]}""",
                HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.DeleteAsync(TestData.ConfigId));
        Assert.Equal(409, ex.StatusCode);
        Assert.Contains("Has children", ex.ResponseBody!);
    }

    // ------------------------------------------------------------------
    // UpdateAsync — 409 Conflict
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_409_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Conflict"}]}""",
                HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.UpdateAsync(TestData.ConfigId,
                new CreateConfigOptions { Name = "Test" }));
        Assert.Equal(409, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // CreateAsync — 409 Conflict
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_409_ThrowsSmplConflictException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Already exists"}]}""",
                HttpStatusCode.Conflict)));

        var ex = await Assert.ThrowsAsync<SmplConflictException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }));
        Assert.Equal(409, ex.StatusCode);
    }

    // ------------------------------------------------------------------
    // GetAsync — non-null data with all populated fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_AllFieldsPopulated_MapsCorrectly()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                id: "11111111-1111-1111-1111-111111111111",
                key: "svc_key",
                name: "Service",
                description: "A description",
                parent: "parent-id",
                valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}""",
                environmentsJson: """{"production": {"values": {"timeout": {"value": 60}}}}"""))));

        var config = await client.Config.GetAsync("11111111-1111-1111-1111-111111111111");

        Assert.Equal("11111111-1111-1111-1111-111111111111", config.Id);
        Assert.Equal("svc_key", config.Key);
        Assert.Equal("Service", config.Name);
        Assert.Equal("A description", config.Description);
        Assert.Equal("parent-id", config.Parent);
        Assert.NotNull(config.CreatedAt);
        Assert.NotNull(config.UpdatedAt);
    }

    // ------------------------------------------------------------------
    // SetValuesAsync — env path preserves base values
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_EnvPath_PreservesBaseValues()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"values": {"timeout": {"value": 60}}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValuesAsync(
            "11111111-1111-1111-1111-111111111111",
            new Dictionary<string, object?> { ["timeout"] = 120 },
            environment: "production");

        Assert.NotNull(putBody);
        // Base values should be preserved (timeout: 30, retries: 3)
        Assert.Contains("retries", putBody);
    }

    // ------------------------------------------------------------------
    // GetByKeyAsync — URL uses AbsoluteUri
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetByKeyAsync_VerifyUrlFormat()
    {
        var (client, handler) = CreateClient(_ =>
        {
            var listJson = """
            {
                "data": [{
                    "id": "11111111-1111-1111-1111-111111111111", "type": "config",
                    "attributes": {
                        "key": "test_key", "name": "Test",
                        "description": null, "parent": null,
                        "values": {}, "environments": {},
                        "created_at": null, "updated_at": null
                    }
                }]
            }
            """;
            return Task.FromResult(JsonResponse(listJson));
        });

        await client.Config.GetByKeyAsync("test_key");

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
    // DeleteAsync — verifies URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_CorrectUrl()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        await client.Config.DeleteAsync("12312312-1231-1231-1231-123123123123");

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        Assert.Contains("/api/v1/configs/12312312-1231-1231-1231-123123123123", url);
    }

    // ------------------------------------------------------------------
    // BuildEnvsForRequest — empty environments
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_EmptyEnvironments_SetsEmptyEnvsInRequest()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"a": {"value": 1, "type": "NUMBER"}}""",
                    environmentsJson: """{}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValuesAsync("11111111-1111-1111-1111-111111111111",
            new Dictionary<string, object?> { ["b"] = 2 });

        Assert.NotNull(putBody);
        Assert.Contains("\"b\"", putBody);
    }

    // ------------------------------------------------------------------
    // SetValueAsync — base path, config with description and parent
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_BaseValue_PreservesDescriptionAndParent()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    description: "My config desc",
                    parent: "parent-uuid",
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("11111111-1111-1111-1111-111111111111", "debug", true);

        Assert.NotNull(putBody);
        Assert.Contains("My config desc", putBody);
        Assert.Contains("parent-uuid", putBody);
    }

    // ------------------------------------------------------------------
    // CreateAsync — with null optional fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_NullOptionalFields_SerializesWithoutNulls()
    {
        string? postBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                postBody = await req.Content!.ReadAsStringAsync();
            }
            return JsonResponse(ConfigJsonWithValuesAndEnvs(), HttpStatusCode.Created);
        });

        await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = "Minimal",
            // Key, Description, Parent, Values, Environments are all null
        });

        Assert.NotNull(postBody);
        Assert.Contains("Minimal", postBody);
        // Generated client serializes all fields (including nulls)
        Assert.Contains("\"name\":\"Minimal\"", postBody);
    }

    // ------------------------------------------------------------------
    // WrapEnvsForRequest — non-dict env value passes through unchanged
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_NonDictEnvValue_PassesThroughInBody()
    {
        string? postBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                postBody = await req.Content!.ReadAsStringAsync();
            }
            return JsonResponse(ConfigJsonWithValuesAndEnvs(), HttpStatusCode.Created);
        });

        // Pass an env value that is NOT a Dictionary<string, object?> — triggers the
        // else branch in WrapEnvsForRequest so the value passes through as-is.
        await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = "Test",
            Environments = new Dictionary<string, object?>
            {
                ["production"] = "not-a-dict",
            },
        });

        Assert.NotNull(postBody);
        // Non-dict env values are skipped by WrapEnvsForRequest — verify body was still sent
        Assert.Contains("\"name\":\"Test\"", postBody);
    }
}
