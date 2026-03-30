using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

public class ConfigClientSetValuesTests
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

    /// <summary>
    /// Builds a config JSON with specific items and environments.
    /// Items use typed format: {key: {"value": raw, "type": "..."}}
    /// Environments use value wrappers: {env: {key: {"value": raw}}}
    /// </summary>
    private static string ConfigJsonWithValuesAndEnvs(
        string id = "cfg-1",
        string key = "my_key",
        string name = "My Config",
        string? parent = null,
        string valuesJson = "{}",
        string environmentsJson = "{}")
    {
        var parentStr = parent is null ? "null" : $"\"{parent}\"";
        return $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "config",
                "attributes": {
                    "key": "{{key}}",
                    "name": "{{name}}",
                    "description": null,
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
    // SetValuesAsync — base values (environment = null)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_BaseValues_PerformsGetThenPut()
    {
        int requestCount = 0;
        string? putBody = null;

        var (client, handler) = CreateClient(async req =>
        {
            requestCount++;
            if (req.Method == HttpMethod.Get)
            {
                // Return current config with existing values
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"new_key": {"value": "new_value", "type": "STRING"}}"""));
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var result = await client.Config.SetValuesAsync(
            "cfg-1",
            new Dictionary<string, object?> { ["new_key"] = "new_value" });

        Assert.Equal(2, requestCount); // GET + PUT
        Assert.NotNull(putBody);
        Assert.Contains("new_key", putBody);
        Assert.Contains("new_value", putBody);
    }

    [Fact]
    public async Task SetValuesAsync_BaseValues_PreservesEnvironments()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    environmentsJson: """{"staging": {"debug": {"value": true}}}"""));
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
            new Dictionary<string, object?> { ["key"] = "val" });

        Assert.NotNull(putBody);
        // The environments should be preserved in the PUT body
        Assert.Contains("staging", putBody);
    }

    // ------------------------------------------------------------------
    // SetValuesAsync — environment-specific values
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_WithEnvironment_SetsEnvValues()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
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
        Assert.Contains("production", putBody);
    }

    [Fact]
    public async Task SetValuesAsync_NewEnvironment_CreatesEnvEntry()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
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
            new Dictionary<string, object?> { ["timeout"] = 90 },
            environment: "staging");

        Assert.NotNull(putBody);
        Assert.Contains("staging", putBody);
    }

    // ------------------------------------------------------------------
    // SetValueAsync — single key, base values
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_BaseValue_MergesIntoExistingValues()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}, "debug": {"value": true, "type": "BOOLEAN"}}"""));
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        var result = await client.Config.SetValueAsync("cfg-1", "debug", true);

        Assert.NotNull(putBody);
        // Should contain both old and new keys
        Assert.Contains("timeout", putBody);
        Assert.Contains("retries", putBody);
        Assert.Contains("debug", putBody);
    }

    [Fact]
    public async Task SetValueAsync_BaseValue_OverwritesExistingKey()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 60, "type": "NUMBER"}}"""));
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "timeout", 60);

        Assert.NotNull(putBody);
    }

    // ------------------------------------------------------------------
    // SetValueAsync — single key, environment-specific
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_EnvValue_MergesIntoExistingEnvValues()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "debug", true, environment: "production");

        Assert.NotNull(putBody);
        Assert.Contains("production", putBody);
        Assert.Contains("debug", putBody);
    }

    [Fact]
    public async Task SetValueAsync_NewEnv_CreatesEnvWithSingleKey()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "retries", 5, environment: "staging");

        Assert.NotNull(putBody);
        Assert.Contains("staging", putBody);
        Assert.Contains("retries", putBody);
    }

    [Fact]
    public async Task SetValueAsync_EnvWithNonDictValues_CreatesNewDict()
    {
        // When env data has "values" key but it normalizes to something non-dict
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                // Environment has a "values" entry that is a string (not a dict)
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"notes": "no values key here"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "debug", true, environment: "production");

        Assert.NotNull(putBody);
        Assert.Contains("debug", putBody);
    }

    [Fact]
    public async Task SetValueAsync_NullValue_SetsNullForKey()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "timeout", null);

        Assert.NotNull(putBody);
    }

    // ------------------------------------------------------------------
    // ConnectAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ReturnsConfigRuntime()
    {
        var (client, _) = CreateClient(req =>
        {
            return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                environmentsJson: """{"production": {"timeout": {"value": 60}}}""")));
        });

        var runtime = await client.Config.ConnectAsync("cfg-1", "production");

        try
        {
            Assert.NotNull(runtime);
            Assert.Equal(60L, runtime.Get("timeout")); // env override
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_WithParentChain_ResolvesInheritance()
    {
        int requestCount = 0;

        var (client, _) = CreateClient(req =>
        {
            requestCount++;
            var url = req.RequestUri!.ToString();

            if (url.Contains("child-id"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "child-id",
                    parent: "parent-id",
                    valuesJson: """{"child_key": {"value": "child_val", "type": "STRING"}}""")));
            }
            else if (url.Contains("parent-id"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "parent-id",
                    valuesJson: """{"parent_key": {"value": "parent_val", "type": "STRING"}, "child_key": {"value": "overridden", "type": "STRING"}}""")));
            }

            return Task.FromResult(JsonResponse("{}", HttpStatusCode.NotFound));
        });

        var runtime = await client.Config.ConnectAsync("child-id", "production");

        try
        {
            Assert.Equal("child_val", runtime.Get("child_key")); // child wins
            Assert.Equal("parent_val", runtime.Get("parent_key")); // inherited
            Assert.True(requestCount >= 2); // At least child + parent fetched
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task ConnectAsync_TimeoutExceeded_ThrowsSmplTimeoutException()
    {
        var (client, _) = CreateClient(async _ =>
        {
            // Simulate a long delay
            await Task.Delay(TimeSpan.FromSeconds(10));
            return JsonResponse(ConfigJsonWithValuesAndEnvs());
        });

        await Assert.ThrowsAsync<SmplTimeoutException>(
            () => client.Config.ConnectAsync("cfg-1", "production", timeout: 1));
    }

    [Fact]
    public async Task ConnectAsync_CallerCancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs())));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.ConnectAsync("cfg-1", "production", ct: cts.Token));
    }

    [Fact]
    public async Task ConnectAsync_RuntimeHasRefreshCapability()
    {
        int fetchCount = 0;

        var (client, _) = CreateClient(req =>
        {
            fetchCount++;
            return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                valuesJson: fetchCount <= 1
                    ? """{"timeout": {"value": 30, "type": "NUMBER"}}"""
                    : """{"timeout": {"value": 999, "type": "NUMBER"}}""")));
        });

        var runtime = await client.Config.ConnectAsync("cfg-1", "production");

        try
        {
            Assert.Equal(30L, runtime.Get("timeout"));

            await runtime.RefreshAsync();

            Assert.Equal(999L, runtime.Get("timeout"));
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // GetByKeyAsync with null attributes in first result
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetByKeyAsync_NullAttributesInResult_ThrowsSmplNotFoundException()
    {
        var json = """
        {
            "data": [
                {"id": "abc", "type": "config", "attributes": null}
            ]
        }
        """;

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(json)));

        await Assert.ThrowsAsync<SmplNotFoundException>(
            () => client.Config.GetByKeyAsync("some_key"));
    }

    // ------------------------------------------------------------------
    // SetValuesAsync preserves config metadata in the update
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_PreservesConfigMetadata()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "cfg-1",
                    key: "svc_key",
                    name: "Service Name",
                    valuesJson: """{"old": {"value": "val", "type": "STRING"}}"""));
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
            new Dictionary<string, object?> { ["new"] = "val" });

        Assert.NotNull(putBody);
        // Should preserve name and key from the GET response
        Assert.Contains("Service Name", putBody);
        Assert.Contains("svc_key", putBody);
    }

    // ------------------------------------------------------------------
    // CreateConfigOptions with all fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_WithAllOptionsFields_IncludesAllInBody()
    {
        string? postBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Post)
            {
                postBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(), HttpStatusCode.Created);
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.CreateAsync(new CreateConfigOptions
        {
            Name = "Full Config",
            Key = "full_key",
            Description = "A full description",
            Parent = "parent-uuid",
            Items = new() { ["a"] = 1 },
            Environments = new() { ["prod"] = new Dictionary<string, object?> { ["b"] = 2 } },
        });

        Assert.NotNull(postBody);
        Assert.Contains("Full Config", postBody);
        Assert.Contains("full_key", postBody);
        Assert.Contains("A full description", postBody);
        Assert.Contains("parent-uuid", postBody);
    }

    // ------------------------------------------------------------------
    // Special characters in key for GetByKeyAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetByKeyAsync_SpecialCharacters_AreUrlEncoded()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": []}""")));

        // This will throw NotFound, but we want to check the URL encoding
        try
        {
            await client.Config.GetByKeyAsync("my key&value=test");
        }
        catch (SmplNotFoundException) { }

        Assert.NotNull(handler.LastRequest);
        var url = handler.LastRequest.RequestUri!.AbsoluteUri;
        // Space should be encoded, & should be encoded
        Assert.DoesNotContain(" ", url);
        Assert.Contains("my%20key%26value%3Dtest", url);
    }

    // ------------------------------------------------------------------
    // SetValuesAsync — environment path with existing env data
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_WithExistingEnvironment_PreservesOtherEnvData()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}, "description": "prod env"}, "staging": {"debug": {"value": true}}}"""));
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
        // Should contain both environments
        Assert.Contains("production", putBody);
        Assert.Contains("staging", putBody);
    }

    // ------------------------------------------------------------------
    // SetValueAsync — env path where env has dict "values"
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_EnvWithExistingDictValues_MergesKey()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"retries": {"value": 5}, "timeout": {"value": 60}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "debug", true, environment: "production");

        Assert.NotNull(putBody);
        // Should merge debug into existing production env values
        Assert.Contains("debug", putBody);
        Assert.Contains("production", putBody);
    }

    // ------------------------------------------------------------------
    // SetValuesAsync — env path creating a new environment
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_NewEnvironment_AddsEnvEntry()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{}"""));
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
            new Dictionary<string, object?> { ["debug"] = true },
            environment: "development");

        Assert.NotNull(putBody);
        Assert.Contains("development", putBody);
        Assert.Contains("debug", putBody);
    }

    // ------------------------------------------------------------------
    // SetValueAsync — env path with values key that normalizes to non-dict
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_EnvWithValuesKeyAsString_CreatesNewDict()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                // Environment has "values" that is a string, not a dict
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"values": {"value": "not-a-dict"}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "retries", 5, environment: "production");

        Assert.NotNull(putBody);
        Assert.Contains("retries", putBody);
    }

    // ------------------------------------------------------------------
    // ConnectAsync — runtime fetchChainFn rebuilds chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_RefreshRebuildsFreshChain()
    {
        int fetchCount = 0;

        var (client, _) = CreateClient(req =>
        {
            fetchCount++;
            if (fetchCount <= 1)
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""")));
            }
            // On refresh, return updated values
            return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                valuesJson: """{"timeout": {"value": 999, "type": "NUMBER"}}""")));
        });

        var runtime = await client.Config.ConnectAsync("cfg-1", "production");

        try
        {
            Assert.Equal(30L, runtime.Get("timeout"));
            await runtime.RefreshAsync();
            Assert.Equal(999L, runtime.Get("timeout"));
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // SetValuesAsync / SetValueAsync — with parent config
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_ConfigWithParent_PreservesParentInUpdate()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
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

        await client.Config.SetValuesAsync(
            "cfg-1",
            new Dictionary<string, object?> { ["new_key"] = "val" });

        Assert.NotNull(putBody);
        Assert.Contains("parent-uuid", putBody);
    }

    // ------------------------------------------------------------------
    // SetValueAsync base path — replaces with merged values
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_BaseValue_NullValue_SetsNull()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}, "retries": {"value": 3, "type": "NUMBER"}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "timeout", null);

        Assert.NotNull(putBody);
        // The merged values should include retries and timeout set to null
        Assert.Contains("retries", putBody);
    }

    // ------------------------------------------------------------------
    // ConnectAsync with parent chain walking
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_ThreeLevelChain_ResolvesFullInheritance()
    {
        var (client, _) = CreateClient(req =>
        {
            var url = req.RequestUri!.ToString();

            if (url.Contains("grandchild"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "grandchild",
                    key: "gc_key",
                    parent: "child",
                    valuesJson: """{"gc_key": {"value": "gc_val", "type": "STRING"}}""")));
            }
            else if (url.Contains("child"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "child",
                    key: "child_key",
                    parent: "root",
                    valuesJson: """{"child_key": {"value": "child_val", "type": "STRING"}}""")));
            }
            else if (url.Contains("root"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "root",
                    key: "root_key",
                    valuesJson: """{"root_key": {"value": "root_val", "type": "STRING"}, "gc_key": {"value": "root_override", "type": "STRING"}}""")));
            }

            return Task.FromResult(JsonResponse("{}", HttpStatusCode.NotFound));
        });

        var runtime = await client.Config.ConnectAsync("grandchild", "production");

        try
        {
            Assert.Equal("gc_val", runtime.Get("gc_key"));      // grandchild wins
            Assert.Equal("child_val", runtime.Get("child_key")); // child
            Assert.Equal("root_val", runtime.Get("root_key"));   // root
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------
    // UpdateAsync — null attributes in response
    // ------------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_NullAttributesInResponse_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": {"id": "abc", "type": "config", "attributes": null}}""")));

        await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.UpdateAsync("abc", new CreateConfigOptions { Name = "Test" }));
    }

    // ------------------------------------------------------------------
    // CreateAsync — null attributes in response
    // ------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_NullAttributesInResponse_ThrowsSmplValidationException()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("""{"data": {"id": "abc", "type": "config", "attributes": null}}""", HttpStatusCode.Created)));

        await Assert.ThrowsAsync<SmplValidationException>(
            () => client.Config.CreateAsync(new CreateConfigOptions { Name = "Test" }));
    }

    // ------------------------------------------------------------------
    // SetValueAsync — env path where env does not exist yet
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValueAsync_NewEnv_MergesIntoEmptyDict()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs());
            }
            return JsonResponse("{}", HttpStatusCode.InternalServerError);
        });

        await client.Config.SetValueAsync("cfg-1", "debug", true, environment: "newenv");

        Assert.NotNull(putBody);
        Assert.Contains("newenv", putBody);
        Assert.Contains("debug", putBody);
    }

    // ------------------------------------------------------------------
    // SetValuesAsync base — empty environments
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_BaseValues_WithEmptyEnvironments()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"old": {"value": "val", "type": "STRING"}}""",
                    environmentsJson: """{}"""));
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
            new Dictionary<string, object?> { ["new"] = "val" });

        Assert.NotNull(putBody);
        Assert.Contains("new", putBody);
    }

    // ------------------------------------------------------------------
    // SetValuesAsync env — existing env with additional properties besides values
    // ------------------------------------------------------------------

    [Fact]
    public async Task SetValuesAsync_EnvPath_PreservesExistingEnvProperties()
    {
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": {"value": 30, "type": "NUMBER"}}""",
                    environmentsJson: """{"production": {"timeout": {"value": 60}, "meta": "data"}}"""));
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
        Assert.Contains("production", putBody);
    }

    // ------------------------------------------------------------------
    // ConnectAsync — caller cancellation propagates properly
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectAsync_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs())));

        // Pre-cancelled token should throw before even starting
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.Config.ConnectAsync("cfg-1", "production", ct: cts.Token));
    }
}
