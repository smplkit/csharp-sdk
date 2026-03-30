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
        string id = "cfg-1",
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
    // ConnectAsync — timeout with non-cancelled caller token
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_InternalTimeout_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return JsonResponse(ConfigJsonWithValuesAndEnvs());
        });

        var ex = await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.ConnectAsync("cfg-1", "production", timeout: 1));

        Assert.Contains("timed out", ex.Message);
        Assert.Contains("1 seconds", ex.Message);
    }

    // ------------------------------------------------------------------
    // ConnectAsync — HTTP error during chain building
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_NotFound_ThrowsSmplNotFoundException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.ConnectAsync("nonexistent", "production"));
    }

    // ------------------------------------------------------------------
    // ConnectAsync — connection error during chain building
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ConnectionError_ThrowsSmplConnectionException()
    {
        var (client, _) = CreateClient(_ =>
            throw new HttpRequestException("Connection refused"));

        await Assert.ThrowsAsync<SmplConnectionException>(
            () => client.Config.ConnectAsync("cfg-1", "production"));
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
            () => client.Config.SetValuesAsync("cfg-1",
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
            () => client.Config.SetValuesAsync("cfg-1",
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
            () => client.Config.SetValueAsync("cfg-1", "key", "val"));
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
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}""")));
            }
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Server error"}]}""",
                HttpStatusCode.InternalServerError));
        });

        await Assert.ThrowsAsync<SmplException>(
            () => client.Config.SetValueAsync("cfg-1", "debug", true, environment: "production"));
    }

    // ------------------------------------------------------------------
    // BuildChainAsync — multi-level parent chain with error on parent fetch
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ParentFetchFails_ThrowsSmplException()
    {
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // Child config has a parent
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "child", parent: "parent-id",
                    valuesJson: """{"child_key": {"value": "val", "type": "STRING"}}""")));
            }
            // Parent fetch fails
            return Task.FromResult(JsonResponse(
                """{"errors":[{"detail":"Not found"}]}""",
                HttpStatusCode.NotFound));
        });

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.ConnectAsync("child", "production"));
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
                id: "cfg-1",
                key: "svc_key",
                name: "Service",
                description: "A description",
                parent: "parent-id",
                valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}""",
                environmentsJson: """{"production": {"timeout": {"value": 60}}}"""))));

        var config = await client.Config.GetAsync("cfg-1");

        Assert.Equal("cfg-1", config.Id);
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
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValuesAsync(
            "cfg-1",
            new Dictionary<string, object?> { ["timeout"] = 120 },
            environment: "production");

        Assert.NotNull(putBody);
        // Base values should be preserved (timeout: 30, retries: 3)
        Assert.Contains("retries", putBody);
    }

    // ------------------------------------------------------------------
    // ConnectAsync — runtime stats match chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_RuntimeStats_MatchChain()
    {
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("child"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "child", parent: "parent",
                    valuesJson: """{"child_key": {"value": "c", "type": "STRING"}}""")));
            }
            return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                id: "parent",
                valuesJson: """{"parent_key": {"value": "p", "type": "STRING"}}""")));
        });

        var runtime = await client.Config.ConnectAsync("child", "production");
        try
        {
            var stats = runtime.Stats();
            Assert.Equal(2, stats.FetchCount); // 2 entries in chain
            Assert.NotNull(stats.LastFetchAt);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
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
                    "id": "cfg-1", "type": "config",
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
        Assert.Contains("filter[key]=test_key", url);
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

        await client.Config.DeleteAsync("cfg-123");

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        Assert.Contains("/api/v1/configs/cfg-123", url);
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

        await client.Config.SetValuesAsync("cfg-1",
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

        await client.Config.SetValueAsync("cfg-1", "debug", true);

        Assert.NotNull(putBody);
        Assert.Contains("My config desc", putBody);
        Assert.Contains("parent-uuid", putBody);
    }

    // ------------------------------------------------------------------
    // ConnectAsync — runtime can be disposed immediately
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ImmediateDispose_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""))));

        var runtime = await client.Config.ConnectAsync("cfg-1", "production");
        await runtime.DisposeAsync();
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
        // Null fields should be omitted due to WhenWritingNull
        Assert.DoesNotContain("\"description\"", postBody);
        Assert.DoesNotContain("\"parent\"", postBody);
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
        Assert.Contains("production", postBody);
    }
}
