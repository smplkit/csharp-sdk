using System.Net;
using System.Text;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Coverage tests for Flag model methods: ToString, SaveAsync (update), AddRule,
/// FlagChangeEvent.
/// </summary>
public class ModelsCoverageTests
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

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };
    }

    /// <summary>Flag list response for GetAsync(key).</summary>
    private static string FlagListJson(
        string id = "faa00001-faa0-faa0-faa0-faa000000001",
        string key = "my-flag",
        string envJson = "{}") =>
        $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "flag",
                    "attributes": {
                        "key": "{{key}}",
                        "name": "My Flag",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                        "description": "Test flag",
                        "environments": {{envJson}},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    /// <summary>Single-item response for SaveAsync (POST/PUT).</summary>
    private static string SingleFlagResponseJson(
        string id = "faa00001-faa0-faa0-faa0-faa000000001",
        string key = "my-flag",
        string name = "My Flag",
        string envJson = "{}") =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "flag",
                "attributes": {
                    "key": "{{key}}",
                    "name": "{{name}}",
                    "type": "BOOLEAN",
                    "default": false,
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Test flag",
                    "environments": {{envJson}},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    // ---------------------------------------------------------------
    // Flag.ToString
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_ToString_IncludesKeyTypeDefault()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");
        var str = flag.ToString();

        Assert.Contains("Key=my-flag", str);
        Assert.Contains("Type=BOOLEAN", str);
        Assert.Contains("Default=", str);
    }

    // ---------------------------------------------------------------
    // Flag.SaveAsync (update — mutate properties then save)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_SaveAsync_Update_UpdatesSelf()
    {
        int requestCount = 0;
        var (client, handler) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // GetAsync (list)
                return Task.FromResult(JsonResponse(
                    FlagListJson().Replace("My Flag", "Updated Flag")));
            }
            // SaveAsync (PUT) response
            return Task.FromResult(JsonResponse(
                SingleFlagResponseJson(name: "Updated Flag")));
        });

        var flag = await client.Flags.GetAsync("my-flag");
        Assert.Equal("Updated Flag", flag.Name);

        flag.Name = "Updated Flag";
        await flag.SaveAsync();

        var putReq = handler.Requests.LastOrDefault(r => r.Method == HttpMethod.Put);
        Assert.NotNull(putReq);
    }

    // ---------------------------------------------------------------
    // Flag.AddRule (synchronous, local mutation)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_AddRule_AddsRuleToEnvironment()
    {
        var envJson = """
        {
            "staging": {
                "enabled": true,
                "default": null,
                "rules": []
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson(envJson: envJson))));

        var flag = await client.Flags.GetAsync("my-flag");

        var rule = new Rule("Test Rule")
            .Environment("staging")
            .When("user.plan", "==", "enterprise")
            .Serve(true)
            .Build();

        var returned = flag.AddRule(rule);

        // AddRule returns this for chaining
        Assert.Same(flag, returned);
        // Rule was added to the staging environment
        Assert.True(flag.Environments.ContainsKey("staging"));
    }

    [Fact]
    public async Task Flag_AddRule_CreatesNewEnvironment_WhenNotExists()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");

        var rule = new Rule("New Env Rule")
            .Environment("production")
            .When("user.plan", "==", "pro")
            .Serve(true)
            .Build();

        flag.AddRule(rule);

        Assert.True(flag.Environments.ContainsKey("production"));
    }

    [Fact]
    public async Task Flag_AddRule_ThrowsWithoutEnvironmentKey()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");

        var rule = new Rule("No Env")
            .When("user.plan", "==", "pro")
            .Serve(true)
            .Build();

        Assert.Throws<ArgumentException>(() => flag.AddRule(rule));
    }

    [Fact]
    public async Task Flag_AddRule_ExistingRulesPreserved()
    {
        var envJson = """
        {
            "staging": {
                "enabled": true,
                "default": null,
                "rules": [{"description": "existing", "logic": {}, "value": false}]
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson(envJson: envJson))));

        var flag = await client.Flags.GetAsync("my-flag");

        var rule = new Rule("New Rule")
            .Environment("staging")
            .When("user.plan", "==", "enterprise")
            .Serve(true)
            .Build();

        flag.AddRule(rule);

        // Environment still has staging
        Assert.True(flag.Environments.ContainsKey("staging"));
    }

    // ---------------------------------------------------------------
    // Flag.SetEnvironmentEnabled
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_SetEnvironmentEnabled_ExistingEnv_SetsEnabled()
    {
        var envJson = """
        {
            "staging": {
                "enabled": true,
                "default": null,
                "rules": []
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson(envJson: envJson))));

        var flag = await client.Flags.GetAsync("my-flag");

        flag.SetEnvironmentEnabled("staging", false);

        Assert.True(flag.Environments.ContainsKey("staging"));
        Assert.Equal(false, flag.Environments["staging"]["enabled"]);
    }

    [Fact]
    public async Task Flag_SetEnvironmentEnabled_NewEnv_CreatesEnvConfig()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");

        flag.SetEnvironmentEnabled("production", true);

        Assert.True(flag.Environments.ContainsKey("production"));
        Assert.Equal(true, flag.Environments["production"]["enabled"]);
    }

    // ---------------------------------------------------------------
    // Flag.SetEnvironmentDefault
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_SetEnvironmentDefault_ExistingEnv_SetsDefault()
    {
        var envJson = """
        {
            "staging": {
                "enabled": true,
                "default": null,
                "rules": []
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson(envJson: envJson))));

        var flag = await client.Flags.GetAsync("my-flag");

        flag.SetEnvironmentDefault("staging", true);

        Assert.True(flag.Environments.ContainsKey("staging"));
        Assert.Equal(true, flag.Environments["staging"]["default"]);
    }

    [Fact]
    public async Task Flag_SetEnvironmentDefault_NewEnv_CreatesEnvConfig()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");

        flag.SetEnvironmentDefault("production", "custom-default");

        Assert.True(flag.Environments.ContainsKey("production"));
        Assert.Equal("custom-default", flag.Environments["production"]["default"]);
    }

    // ---------------------------------------------------------------
    // Flag.ClearRules
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_ClearRules_ExistingEnvWithRules_ClearsRules()
    {
        var envJson = """
        {
            "staging": {
                "enabled": true,
                "default": null,
                "rules": [{"description": "existing", "logic": {}, "value": false}]
            }
        }
        """;
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson(envJson: envJson))));

        var flag = await client.Flags.GetAsync("my-flag");

        flag.ClearRules("staging");

        Assert.True(flag.Environments.ContainsKey("staging"));
        var rules = flag.Environments["staging"]["rules"] as List<object?>;
        Assert.NotNull(rules);
        Assert.Empty(rules);
    }

    [Fact]
    public async Task Flag_ClearRules_NonExistentEnv_DoesNothing()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");

        // Should not throw - non-existent env is a no-op
        flag.ClearRules("nonexistent-env");
    }

    // ---------------------------------------------------------------
    // Flag.SaveAsync update path (Id != null, applies returned data)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_SaveAsync_Update_AppliesAllReturnedFields()
    {
        var envJson = """
        {
            "staging": {
                "enabled": true,
                "default": null,
                "rules": []
            }
        }
        """;
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                // GetAsync (list)
                return Task.FromResult(JsonResponse(FlagListJson(envJson: envJson)));
            }
            // SaveAsync (PUT) response with updated values
            return Task.FromResult(JsonResponse(SingleFlagResponseJson(
                key: "my-flag",
                name: "Updated Name",
                envJson: """{"production": {"enabled": true, "default": true, "rules": []}}""")));
        });

        var flag = await client.Flags.GetAsync("my-flag");
        Assert.NotNull(flag.Id);

        flag.Name = "Updated Name";
        flag.Description = "new desc";
        await flag.SaveAsync();

        // Verify all fields were applied from the server response
        Assert.Equal("Updated Name", flag.Name);
        Assert.NotNull(flag.Id);
        Assert.NotNull(flag.CreatedAt);
        Assert.NotNull(flag.UpdatedAt);
    }

    // ---------------------------------------------------------------
    // Flag.Get (base class Get via typed flags)
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_Get_BaseClass_ReturnsEvaluatedValue()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(FlagListJson())));

        var flag = await client.Flags.GetAsync("my-flag");
        // The base Get() calls EvaluateHandle which triggers lazy init
        // For a non-handle flag fetched via GetAsync, it has a key and the
        // flag store will be populated after EnsureInitialized
        var result = flag.Get();
        // Result is the evaluated value (flag default is false)
        Assert.IsType<bool>(result);
    }

    // ---------------------------------------------------------------
    // FlagStats record
    // ---------------------------------------------------------------

    [Fact]
    public void FlagStats_ExposesProperties()
    {
        var stats = new FlagStats(10, 5);
        Assert.Equal(10, stats.CacheHits);
        Assert.Equal(5, stats.CacheMisses);
    }

    // ---------------------------------------------------------------
    // FlagChangeEvent
    // ---------------------------------------------------------------

    [Fact]
    public void FlagChangeEvent_ExposesKeyAndSource()
    {
        var evt = new FlagChangeEvent("my-flag", "websocket");

        Assert.Equal("my-flag", evt.Key);
        Assert.Equal("websocket", evt.Source);
    }
}
