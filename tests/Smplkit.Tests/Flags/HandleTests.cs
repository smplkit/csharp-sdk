using System.Net;
using System.Text;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

public class HandleTests
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

    private static string SimpleFlagListJson(
        string key,
        string type,
        string defaultVal) =>
        $$"""
        {
            "data": [
                {
                    "id": "flag-001",
                    "type": "flag",
                    "attributes": {
                        "key": "{{key}}",
                        "name": "Test Flag",
                        "type": "{{type}}",
                        "default": {{defaultVal}},
                        "values": [],
                        "description": null,
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    // ---------------------------------------------------------------
    // BoolFlagHandle
    // ---------------------------------------------------------------

    [Fact]
    public void BoolFlag_ReturnsDefault_WhenNotConnected()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.BoolFlag("my-flag", true);

        Assert.True(handle.Get());
    }

    [Fact]
    public void BoolFlag_ReturnsCodeDefault_WhenNotConnected_False()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.BoolFlag("my-flag", false);

        Assert.False(handle.Get());
    }

    [Fact]
    public async Task BoolFlag_ReturnsFlagDefault_WhenConnected_NoRuleMatch()
    {
        var flagJson = SimpleFlagListJson("my-bool", "BOOLEAN", "true");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BoolFlag("my-bool", false);
        await client.Flags.ConnectAsync("production");

        // Flag exists in store, so EvaluateHandle evaluates it.
        // No environment rules match, returns flag default (true from JSON).
        var result = handle.Get();
        Assert.True(result);
    }

    [Fact]
    public async Task BoolFlag_ReturnsCodeDefault_WhenFlagNotInStore()
    {
        // Flag list has a different key than what the handle is looking for
        var flagJson = SimpleFlagListJson("other-flag", "BOOLEAN", "true");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BoolFlag("my-bool", false);
        await client.Flags.ConnectAsync("production");

        // Flag key not found in store, returns code default
        Assert.False(handle.Get());
    }

    // ---------------------------------------------------------------
    // StringFlagHandle
    // ---------------------------------------------------------------

    [Fact]
    public void StringFlag_ReturnsDefault_WhenNotConnected()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.StringFlag("my-str", "fallback");

        Assert.Equal("fallback", handle.Get());
    }

    [Fact]
    public async Task StringFlag_ReturnsFlagDefault_WhenConnected()
    {
        var flagJson = SimpleFlagListJson("my-str", "STRING", "\"server-default\"");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.StringFlag("my-str", "code-default");
        await client.Flags.ConnectAsync("production");

        var result = handle.Get();
        Assert.Equal("server-default", result);
    }

    [Fact]
    public async Task StringFlag_ReturnsCodeDefault_WhenWrongType()
    {
        // Flag default is a number, not a string
        var flagJson = SimpleFlagListJson("my-str", "STRING", "42");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.StringFlag("my-str", "code-default");
        await client.Flags.ConnectAsync("production");

        // EvaluateFlag returns 42 (long), which is not a string
        // StringFlagHandle.Get falls back to code default
        var result = handle.Get();
        Assert.Equal("code-default", result);
    }

    // ---------------------------------------------------------------
    // NumberFlagHandle
    // ---------------------------------------------------------------

    [Fact]
    public void NumberFlag_ReturnsDefault_WhenNotConnected()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("my-num", 3.14);

        Assert.Equal(3.14, handle.Get());
    }

    [Fact]
    public async Task NumberFlag_ReturnsFlagDefault_WhenConnected()
    {
        var flagJson = SimpleFlagListJson("my-num", "NUMERIC", "99");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("my-num", 0.0);
        await client.Flags.ConnectAsync("production");

        // Flag default from server is 99 (parsed as long), NumberFlagHandle converts to double
        var result = handle.Get();
        Assert.Equal(99.0, result);
    }

    [Fact]
    public async Task NumberFlag_ReturnsCodeDefault_WhenWrongType()
    {
        // Flag default is a string
        var flagJson = SimpleFlagListJson("my-num", "NUMERIC", "\"not-a-number\"");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("my-num", 5.5);
        await client.Flags.ConnectAsync("production");

        var result = handle.Get();
        Assert.Equal(5.5, result);
    }

    [Fact]
    public async Task NumberFlag_HandlesDouble()
    {
        var flagJson = SimpleFlagListJson("my-num", "NUMERIC", "3.14");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("my-num", 0.0);
        await client.Flags.ConnectAsync("production");

        var result = handle.Get();
        Assert.Equal(3.14, result);
    }

    // ---------------------------------------------------------------
    // JsonFlagHandle
    // ---------------------------------------------------------------

    [Fact]
    public void JsonFlag_ReturnsDefault_WhenNotConnected()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var defaultDict = new Dictionary<string, object?> { ["theme"] = "light" };
        var handle = client.Flags.JsonFlag("my-json", defaultDict);

        var result = handle.Get();
        Assert.Equal("light", result["theme"]);
    }

    [Fact]
    public async Task JsonFlag_ReturnsFlagDefault_WhenConnected()
    {
        var flagJson = SimpleFlagListJson("my-json", "JSON", """{"theme": "dark"}""");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var defaultDict = new Dictionary<string, object?> { ["theme"] = "light" };
        var handle = client.Flags.JsonFlag("my-json", defaultDict);
        await client.Flags.ConnectAsync("production");

        var result = handle.Get();
        Assert.Equal("dark", result["theme"]?.ToString());
    }

    [Fact]
    public async Task JsonFlag_ReturnsCodeDefault_WhenWrongType()
    {
        // Flag default is a string, not a dict
        var flagJson = SimpleFlagListJson("my-json", "JSON", "\"not-a-dict\"");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var defaultDict = new Dictionary<string, object?> { ["a"] = 1 };
        var handle = client.Flags.JsonFlag("my-json", defaultDict);
        await client.Flags.ConnectAsync("production");

        var result = handle.Get();
        Assert.Equal(1, result["a"]);
    }

    // ---------------------------------------------------------------
    // OnChange
    // ---------------------------------------------------------------

    [Fact]
    public void OnChange_RegistersListener()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.BoolFlag("my-flag", false);
        var events = new List<FlagChangeEvent>();

        handle.OnChange(evt => events.Add(evt));

        Assert.Single(handle.Listeners);
    }

    [Fact]
    public void OnChange_MultipleListeners()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.StringFlag("my-flag", "default");

        handle.OnChange(_ => { });
        handle.OnChange(_ => { });
        handle.OnChange(_ => { });

        Assert.Equal(3, handle.Listeners.Count);
    }

    // ---------------------------------------------------------------
    // Get with explicit context override
    // ---------------------------------------------------------------

    [Fact]
    public async Task Handle_GetWithContext_PassesContextToEvaluation()
    {
        var flagJson = SimpleFlagListJson("ctx-flag", "BOOLEAN", "false");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BoolFlag("ctx-flag", true);
        await client.Flags.ConnectAsync("production");

        // Pass an explicit context -- the flag has no environment rules so returns flag default (false)
        var contexts = new List<Context>
        {
            new("user", "u1", new Dictionary<string, object?> { ["plan"] = "enterprise" }),
        };
        var result = handle.Get(contexts);

        Assert.False(result); // flag default is false
    }

    [Fact]
    public void Handle_GetWithContext_ReturnsCodeDefault_WhenNotConnected()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.BoolFlag("ctx-flag", true);

        var contexts = new List<Context>
        {
            new("user", "u1", new Dictionary<string, object?> { ["plan"] = "enterprise" }),
        };
        var result = handle.Get(contexts);

        Assert.True(result); // returns code default when not connected
    }

    // ---------------------------------------------------------------
    // Handle key and default
    // ---------------------------------------------------------------

    [Fact]
    public void Handle_ExposesKeyAndDefault()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        var boolHandle = client.Flags.BoolFlag("bool-key", true);
        Assert.Equal("bool-key", boolHandle.Key);
        Assert.Equal(true, boolHandle.Default);

        var strHandle = client.Flags.StringFlag("str-key", "default-val");
        Assert.Equal("str-key", strHandle.Key);
        Assert.Equal("default-val", strHandle.Default);

        var numHandle = client.Flags.NumberFlag("num-key", 42.0);
        Assert.Equal("num-key", numHandle.Key);
        Assert.Equal(42.0, numHandle.Default);

        var jsonDefault = new Dictionary<string, object?> { ["x"] = 1 };
        var jsonHandle = client.Flags.JsonFlag("json-key", jsonDefault);
        Assert.Equal("json-key", jsonHandle.Key);
        Assert.Same(jsonDefault, jsonHandle.Default);
    }

    // ---------------------------------------------------------------
    // Cache behavior through handles
    // ---------------------------------------------------------------

    [Fact]
    public async Task Handle_UsesCache_OnRepeatedGet()
    {
        var flagJson = SimpleFlagListJson("cached-flag", "BOOLEAN", "true");
        var (client, _) = CreateClient(_ =>
            Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BoolFlag("cached-flag", false);
        await client.Flags.ConnectAsync("production");

        // First call evaluates and caches
        var result1 = handle.Get();
        // Second call should hit cache
        var result2 = handle.Get();

        Assert.Equal(result1, result2);
        Assert.True(client.Flags.Stats.CacheHits >= 1);
    }
}
