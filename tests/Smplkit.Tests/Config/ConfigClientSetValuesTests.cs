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
    /// Environments use wire format: {env: {"values": {key: {"value": raw}}}}
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
                    environmentsJson: """{"production": {"values": {"timeout": {"value": 60}}}}"""));
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
                    environmentsJson: """{"staging": {"values": {"debug": {"value": true}}}}"""));
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
                    environmentsJson: """{"production": {"values": {"timeout": {"value": 60}}}}"""));
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
        // When env data has no "values" key, env is treated as empty
        string? putBody = null;

        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                // Environment missing "values" wrapper — no overrides extracted
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
                    environmentsJson: """{"production": {"values": {"timeout": {"value": 60}}}, "staging": {"values": {"debug": {"value": true}}}}"""));
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
                    environmentsJson: """{"production": {"values": {"retries": {"value": 5}, "timeout": {"value": 60}}}}"""));
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
                // Environment has a values item whose wrapper is a scalar (not {"value": ...})
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
            "cfg-1",
            new Dictionary<string, object?> { ["timeout"] = 120 },
            environment: "production");

        Assert.NotNull(putBody);
        Assert.Contains("production", putBody);
    }

}
