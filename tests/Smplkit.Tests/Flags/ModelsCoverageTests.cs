using System.Net;
using System.Text;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Coverage tests for Flag model methods: ToString, UpdateAsync, AddRuleAsync,
/// FlagChangeEvent, ContextType.ToString.
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

    private static string SingleFlagJson(
        string id = "flag-001",
        string key = "my-flag",
        string envJson = "{}") =>
        $$"""
        {
            "data": {
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
        }
        """;

    // ---------------------------------------------------------------
    // Flag.ToString
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_ToString_IncludesKeyTypeDefault()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(SingleFlagJson())));

        var flag = await client.Flags.GetAsync("flag-001");
        var str = flag.ToString();

        Assert.Contains("Key=my-flag", str);
        Assert.Contains("Type=BOOLEAN", str);
        Assert.Contains("Default=", str);
    }

    // ---------------------------------------------------------------
    // Flag.UpdateAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_UpdateAsync_UpdatesSelf()
    {
        var updatedJson = SingleFlagJson(id: "flag-001", key: "my-flag")
            .Replace("My Flag", "Updated Flag");

        var (client, handler) = CreateClient(_ => Task.FromResult(JsonResponse(updatedJson)));

        var flag = await client.Flags.GetAsync("flag-001");
        Assert.Equal("Updated Flag", flag.Name);

        await flag.UpdateAsync(name: "Updated Flag");

        var putReq = handler.Requests.LastOrDefault(r => r.Method == HttpMethod.Put);
        Assert.NotNull(putReq);
    }

    // ---------------------------------------------------------------
    // Flag.AddRuleAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task Flag_AddRuleAsync_AddsRuleToEnvironment()
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
        var flagJsonWithEnv = SingleFlagJson(envJson: envJson);
        var (client, handler) = CreateClient(_ => Task.FromResult(JsonResponse(flagJsonWithEnv)));

        var flag = await client.Flags.GetAsync("flag-001");

        var rule = new Rule("Test Rule")
            .Environment("staging")
            .When("user.plan", "==", "enterprise")
            .Serve(true)
            .Build();

        await flag.AddRuleAsync(rule);

        var putReq = handler.Requests.LastOrDefault(r => r.Method == HttpMethod.Put);
        Assert.NotNull(putReq);
    }

    [Fact]
    public async Task Flag_AddRuleAsync_CreatesNewEnvironment_WhenNotExists()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(SingleFlagJson())));

        var flag = await client.Flags.GetAsync("flag-001");

        var rule = new Rule("New Env Rule")
            .Environment("production")
            .When("user.plan", "==", "pro")
            .Serve(true)
            .Build();

        await flag.AddRuleAsync(rule);
    }

    [Fact]
    public async Task Flag_AddRuleAsync_ThrowsWithoutEnvironmentKey()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(SingleFlagJson())));

        var flag = await client.Flags.GetAsync("flag-001");

        var rule = new Rule("No Env")
            .When("user.plan", "==", "pro")
            .Serve(true)
            .Build();

        await Assert.ThrowsAsync<ArgumentException>(() => flag.AddRuleAsync(rule));
    }

    [Fact]
    public async Task Flag_AddRuleAsync_ExistingRulesPreserved()
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
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(SingleFlagJson(envJson: envJson))));

        var flag = await client.Flags.GetAsync("flag-001");

        var rule = new Rule("New Rule")
            .Environment("staging")
            .When("user.plan", "==", "enterprise")
            .Serve(true)
            .Build();

        await flag.AddRuleAsync(rule);
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

    // ---------------------------------------------------------------
    // ContextType.ToString
    // ---------------------------------------------------------------

    [Fact]
    public async Task ContextType_ToString_IncludesKeyAndName()
    {
        var ctJson = """
        {
            "data": {
                "id": "ct-001",
                "type": "context_type",
                "attributes": {
                    "key": "user",
                    "name": "User",
                    "attributes": {}
                }
            }
        }
        """;
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(ctJson)));

        var ct = await client.Flags.CreateContextTypeAsync("user", "User");
        var str = ct.ToString();

        Assert.Contains("Key=user", str);
        Assert.Contains("Name=User", str);
    }
}
