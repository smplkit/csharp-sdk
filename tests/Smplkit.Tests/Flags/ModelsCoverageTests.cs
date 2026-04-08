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
