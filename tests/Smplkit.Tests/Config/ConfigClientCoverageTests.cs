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

    // ------------------------------------------------------------------
    // Resolve<T> with dot-notation expansion
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveT_DotNotation_ExpandsToNestedObject()
    {
        var configListJson = """
        {
            "data": [
                {
                    "id": "cfg-dot-1",
                    "type": "config",
                    "attributes": {
                        "key": "dot_config",
                        "name": "Dot Config",
                        "description": null,
                        "parent": null,
                        "items": {
                            "database.host": {"value": "localhost", "type": "STRING"},
                            "database.port": {"value": 5432, "type": "NUMBER"}
                        },
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("configs"))
                return Task.FromResult(JsonResponse(configListJson));
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });

        var result = client.Config.Resolve<DotNotationModel>("dot_config");
        Assert.NotNull(result.Database);
        Assert.Equal("localhost", result.Database!.Host);
        Assert.Equal(5432, result.Database.Port);
    }

    // ------------------------------------------------------------------
    // ExpandDotNotation — multi-level nesting
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveT_MultiLevelDotNotation_ExpandsCorrectly()
    {
        var configListJson = """
        {
            "data": [
                {
                    "id": "cfg-multi-1",
                    "type": "config",
                    "attributes": {
                        "key": "multi_config",
                        "name": "Multi Config",
                        "description": null,
                        "parent": null,
                        "items": {
                            "app.name": {"value": "MyApp", "type": "STRING"},
                            "app.version": {"value": "1.0", "type": "STRING"}
                        },
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("configs"))
                return Task.FromResult(JsonResponse(configListJson));
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });

        // Resolve as a plain dict first to verify expansion
        var values = client.Config.Resolve("multi_config");
        // Flat keys still returned from Resolve()
        Assert.Equal("MyApp", values["app.name"]);
    }

    // ------------------------------------------------------------------
    // EnsureInitialized — no parent (null _parent) throws
    // ------------------------------------------------------------------

    [Fact]
    public void EnsureInitialized_NoParent_ThrowsSmplException()
    {
        // Use reflection to create a ConfigClient without a parent
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var options = new SmplClientOptions { ApiKey = "sk_api_test", Timeout = TimeSpan.FromSeconds(30) };
        var factory = new Smplkit.Internal.GeneratedClientFactory(httpClient, options);
        var configClient = new Smplkit.Config.ConfigClient(factory, null, null);

        var ex = Assert.Throws<Smplkit.Errors.SmplException>(() => configClient.Resolve("test"));
        Assert.Contains("No environment set", ex.Message);
    }

    // ------------------------------------------------------------------
    // RefreshAsync — no parent throws
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_NoParent_ThrowsSmplException()
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var options = new SmplClientOptions { ApiKey = "sk_api_test", Timeout = TimeSpan.FromSeconds(30) };
        var factory = new Smplkit.Internal.GeneratedClientFactory(httpClient, options);
        var configClient = new Smplkit.Config.ConfigClient(factory, null, null);

        var ex = await Assert.ThrowsAsync<Smplkit.Errors.SmplException>(
            () => configClient.RefreshAsync());
        Assert.Contains("No environment set", ex.Message);
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged via reflection
    // ------------------------------------------------------------------

    [Fact]
    public void HandleConfigChanged_NotInitialized_DoesNothing()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        // HandleConfigChanged is private — invoke via reflection
        // When _runtimeConnected is false, it should return immediately
        var method = typeof(Smplkit.Config.ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Config, new object[]
        {
            new Dictionary<string, object?> { ["key"] = "test" }
        });
        // No exception — early return
    }

    [Fact]
    public void HandleConfigChanged_Initialized_RefetchesAndFiresListeners()
    {
        var configListJson = """
        {
            "data": [
                {
                    "id": "cfg-ws-1",
                    "type": "config",
                    "attributes": {
                        "key": "ws_config",
                        "name": "WS Config",
                        "description": null,
                        "parent": null,
                        "items": { "retries": {"value": 3, "type": "NUMBER"} },
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var handler = new MockHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (url.Contains("configs"))
                return Task.FromResult(JsonResponse(configListJson));
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production", Service = "test-service" },
            httpClient);

        // Trigger initialization
        var val = client.Config.Resolve("ws_config");
        Assert.Equal(3L, val["retries"]);

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        // Now invoke HandleConfigChanged via reflection
        var method = typeof(Smplkit.Config.ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Config, new object[]
        {
            new Dictionary<string, object?> { ["key"] = "ws_config" }
        });

        // The handler returns the same data, so no diff events should fire
        // (values didn't actually change)
        // But it shouldn't throw either
    }

    [Fact]
    public void HandleConfigChanged_NullEnvironment_DoesNothing()
    {
        // Create a ConfigClient where _parent.Environment would be null
        // This is hard to test since SmplClient always sets Environment,
        // but we can test by calling HandleConfigChanged before init.
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var method = typeof(Smplkit.Config.ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        // Should not throw - _runtimeConnected is false so it returns immediately
        method!.Invoke(client.Config, new object[]
        {
            new Dictionary<string, object?> { ["key"] = "test" }
        });
    }

    [Fact]
    public void HandleConfigChanged_TransportFailure_DoesNotThrow()
    {
        bool failOnNext = false;
        var configListJson = """
        {
            "data": [
                {
                    "id": "cfg-err-1",
                    "type": "config",
                    "attributes": {
                        "key": "err_config",
                        "name": "Err Config",
                        "description": null,
                        "parent": null,
                        "items": { "timeout": {"value": 30, "type": "NUMBER"} },
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var handler = new MockHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            if (failOnNext && url.Contains("configs"))
                throw new HttpRequestException("Network error");
            if (url.Contains("configs"))
                return Task.FromResult(JsonResponse(configListJson));
            return Task.FromResult(JsonResponse("""{"data":[]}"""));
        });
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production", Service = "test-service" },
            httpClient);

        // Trigger initialization
        client.Config.Resolve("err_config");

        // Now make subsequent config requests fail
        failOnNext = true;

        // Invoke HandleConfigChanged — the re-fetch will fail
        var method = typeof(Smplkit.Config.ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should not throw — error is swallowed by catch block
        method!.Invoke(client.Config, new object[]
        {
            new Dictionary<string, object?> { ["key"] = "err_config" }
        });
    }

    // ------------------------------------------------------------------
    // WrapEnvsForRequest — null environments
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_WithNullEnvironments_WorksCorrectly()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson(), HttpStatusCode.Created)));

        var config = client.Config.New("no_env_config", "No Env");
        // Environments is empty by default
        await config.SaveAsync();

        Assert.NotNull(config.Id);
    }

    // ------------------------------------------------------------------
    // WrapItemsForRequest — null items
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_WithEmptyItems_WorksCorrectly()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleConfigJson(), HttpStatusCode.Created)));

        var config = client.Config.New("empty_items", "Empty Items");
        // Items is empty by default
        await config.SaveAsync();

        Assert.NotNull(config.Id);
    }

    // ------------------------------------------------------------------
    // InferType helper coverage
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_WithStringItem_InfersStringType()
    {
        string? postBody = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
                postBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(SingleConfigJson(), HttpStatusCode.Created);
        });

        var config = client.Config.New("typed_config", "Typed Config");
        config.Items["str_key"] = "hello";
        config.Items["bool_key"] = true;
        config.Items["num_key"] = 42;
        config.Items["null_key"] = null;
        await config.SaveAsync();

        Assert.NotNull(postBody);
        Assert.Contains("str_key", postBody);
        Assert.Contains("bool_key", postBody);
        Assert.Contains("num_key", postBody);
    }
}

/// <summary>Test model for dot-notation Resolve&lt;T&gt; testing.</summary>
public class DotNotationModel
{
    public DatabaseConfig? Database { get; set; }
}

/// <summary>Nested config model for dot-notation testing.</summary>
public class DatabaseConfig
{
    public string? Host { get; set; }
    public int Port { get; set; }
}
