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
    private static (SmplkitClient client, MockHttpMessageHandler handler) CreateClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handlerFn)
    {
        var handler = new MockHttpMessageHandler(handlerFn);
        var httpClient = new HttpClient(handler);
        var options = TestData.DefaultOptions();
        var client = new SmplkitClient(options, httpClient);
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
    /// Builds a config JSON with specific values and environments.
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
                    "values": {{valuesJson}},
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
                    valuesJson: """{"timeout": 30, "retries": 3}""",
                    environmentsJson: """{"production": {"values": {"timeout": 60}}}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"new_key": "new_value"}"""));
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
                    environmentsJson: """{"staging": {"values": {"debug": true}}}"""));
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
                    valuesJson: """{"timeout": 30}""",
                    environmentsJson: """{"production": {"values": {"timeout": 60}}}"""));
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
                    valuesJson: """{"timeout": 30}"""));
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
                    valuesJson: """{"timeout": 30, "retries": 3}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": 30, "retries": 3, "debug": true}"""));
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
                    valuesJson: """{"timeout": 30}"""));
            }
            else if (req.Method == HttpMethod.Put)
            {
                putBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(ConfigJsonWithValuesAndEnvs(
                    valuesJson: """{"timeout": 60}"""));
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
                    valuesJson: """{"timeout": 30}""",
                    environmentsJson: """{"production": {"values": {"timeout": 60}}}"""));
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
                    valuesJson: """{"timeout": 30}"""));
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
                    valuesJson: """{"timeout": 30}""",
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
                    valuesJson: """{"timeout": 30}"""));
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
                valuesJson: """{"timeout": 30}""",
                environmentsJson: """{"production": {"values": {"timeout": 60}}}""")));
        });

        var runtime = await client.Config.ConnectAsync("cfg-1", "production");

        try
        {
            Assert.NotNull(runtime);
            Assert.Equal(60, runtime.Get("timeout")); // env override
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
                    valuesJson: """{"child_key": "child_val"}""")));
            }
            else if (url.Contains("parent-id"))
            {
                return Task.FromResult(JsonResponse(ConfigJsonWithValuesAndEnvs(
                    id: "parent-id",
                    valuesJson: """{"parent_key": "parent_val", "child_key": "overridden"}""")));
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
                    ? """{"timeout": 30}"""
                    : """{"timeout": 999}""")));
        });

        var runtime = await client.Config.ConnectAsync("cfg-1", "production");

        try
        {
            Assert.Equal(30, runtime.Get("timeout"));

            await runtime.RefreshAsync();

            Assert.Equal(999, runtime.Get("timeout"));
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
                    valuesJson: """{"old": "val"}"""));
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
            Values = new() { ["a"] = 1 },
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
        var url = handler.LastRequest.RequestUri!.ToString();
        // Space should be encoded, & should be encoded
        Assert.DoesNotContain(" ", url);
        Assert.Contains("my%20key%26value%3Dtest", url);
    }
}
