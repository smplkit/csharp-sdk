using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Tests for flag handle edge cases: JsonElement coercion paths in Get methods.
/// These paths are hit when EvaluateFlag returns a raw JsonElement value
/// (e.g., from rule matching where the value comes from deserialized JSON).
/// </summary>
public class HandleEdgeCaseTests
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

    /// <summary>
    /// Build a flag list JSON where a rule returns a JsonElement value (serialized as raw JSON).
    /// The trick is that when the rule value comes from a JSON-deserialized environment config,
    /// it arrives as a JsonElement, not a native C# type.
    /// </summary>
    private static string FlagListWithRuleJson(string id, string type, string defaultVal, string ruleValue)
    {
        return $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "flag",
                    "attributes": {
                        "id": "{{id}}",
                        "name": "Test Flag",
                        "type": "{{type}}",
                        "default": {{defaultVal}},
                        "values": [],
                        "description": null,
                        "environments": {
                            "test": {
                                "enabled": true,
                                "default": null,
                                "rules": [
                                    {
                                        "description": "always match",
                                        "logic": {"==": [1, 1]},
                                        "value": {{ruleValue}}
                                    }
                                ]
                            }
                        },
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
    }

    // ---------------------------------------------------------------
    // BooleanFlag - JsonElement true/false
    // ---------------------------------------------------------------

    [Fact]
    public void BooleanFlag_JsonElementTrue_ReturnsBool()
    {
        // Rule returns true as a JsonElement (from the JSON deserialization)
        var flagJson = FlagListWithRuleJson("je-bool", "BOOLEAN", "false", "true");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BooleanFlag("je-bool", false);

        // Get() triggers lazy init
        var result = handle.Get();
        Assert.True(result);
    }

    [Fact]
    public void BooleanFlag_NonBoolValue_ReturnsDefault()
    {
        // Rule returns a string value for a bool flag
        var flagJson = FlagListWithRuleJson("je-bool-str", "BOOLEAN", "false", "\"not-a-bool\"");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BooleanFlag("je-bool-str", true);

        // Value is "not-a-bool" which is not bool or JsonElement bool => returns code default
        var result = handle.Get();
        Assert.True(result);
    }

    // ---------------------------------------------------------------
    // StringFlag - JsonElement string
    // ---------------------------------------------------------------

    [Fact]
    public void StringFlag_JsonElementString_ReturnsString()
    {
        var flagJson = FlagListWithRuleJson("je-str", "STRING", "\"default\"", "\"matched-val\"");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.StringFlag("je-str", "code-default");

        var result = handle.Get();
        Assert.Equal("matched-val", result);
    }

    [Fact]
    public void StringFlag_NonStringValue_ReturnsDefault()
    {
        // Rule returns a number for a string flag
        var flagJson = FlagListWithRuleJson("je-str-num", "STRING", "\"default\"", "42");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.StringFlag("je-str-num", "code-default");

        var result = handle.Get();
        Assert.Equal("code-default", result);
    }

    // ---------------------------------------------------------------
    // NumberFlag - JsonElement numeric
    // ---------------------------------------------------------------

    [Fact]
    public void NumberFlag_JsonElementInteger_ReturnsNumber()
    {
        var flagJson = FlagListWithRuleJson("je-num", "NUMERIC", "0", "99");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("je-num", 0.0);

        var result = handle.Get();
        Assert.Equal(99.0, result);
    }

    [Fact]
    public void NumberFlag_JsonElementDouble_ReturnsNumber()
    {
        var flagJson = FlagListWithRuleJson("je-num-dbl", "NUMERIC", "0", "3.14");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("je-num-dbl", 0.0);

        var result = handle.Get();
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void NumberFlag_NonNumericValue_ReturnsDefault()
    {
        var flagJson = FlagListWithRuleJson("je-num-str", "NUMERIC", "0", "\"not-a-number\"");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("je-num-str", 5.5);

        var result = handle.Get();
        Assert.Equal(5.5, result);
    }

    // ---------------------------------------------------------------
    // Direct JsonElement injection via reflection to cover defensive
    // branches in handle Get methods.
    // These paths are defensive: NormalizeValue converts JsonElements
    // to native types, but the handles guard against raw JsonElements
    // in case evaluation bypasses normalization.
    // ---------------------------------------------------------------

    /// <summary>
    /// Helper: injects a raw JsonElement value as the flag's default into the FlagsClient's
    /// internal _flagStore and sets _connected=true. The environment is enabled with no rules,
    /// so the fallback path returns the raw JsonElement without normalization. This exercises
    /// the defensive JsonElement branches in handle Get methods.
    /// </summary>
    private static void InjectRawFlagDef(SmplClient client, string id, string environment, object rawValue)
    {
        var flagsClient = client.Flags;

        // Set _connected = true
        var connectedField = typeof(FlagsClient).GetField("_connected",
            BindingFlags.NonPublic | BindingFlags.Instance);
        connectedField!.SetValue(flagsClient, true);

        // Set _environment
        var envField = typeof(FlagsClient).GetField("_environment",
            BindingFlags.NonPublic | BindingFlags.Instance);
        envField!.SetValue(flagsClient, environment);

        // Access the _flagStore
        var storeField = typeof(FlagsClient).GetField("_flagStore",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var store = (ConcurrentDictionary<string, Dictionary<string, object?>>)storeField!.GetValue(flagsClient)!;

        // Build a flag def with the raw value as the flag-level default.
        // Environment is enabled with no rules, so EvaluateFlag returns the fallback
        // which is envDefault ?? flagDefault. envDefault is the raw JsonElement.
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["default"] = rawValue, // raw JsonElement as env default
            ["rules"] = new List<object?>(),
        };

        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["default"] = rawValue, // raw JsonElement as flag default
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                [environment] = envConfig,
            },
        };

        store[id] = flagDef;
    }

    [Fact]
    public void BooleanFlag_JsonElementValue_ReturnsBoolean()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.BooleanFlag("je-inject-bool", false);

        // Inject a raw JsonElement boolean
        var je = JsonSerializer.Deserialize<JsonElement>("true");
        InjectRawFlagDef(client, "je-inject-bool", "test", je);

        var result = handle.Get();
        Assert.True(result);
    }

    [Fact]
    public void StringFlag_JsonElementValue_ReturnsString()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.StringFlag("je-inject-str", "code-default");

        // Inject a raw JsonElement string
        var je = JsonSerializer.Deserialize<JsonElement>("\"injected-value\"");
        InjectRawFlagDef(client, "je-inject-str", "test", je);

        var result = handle.Get();
        Assert.Equal("injected-value", result);
    }

    [Fact]
    public void NumberFlag_JsonElementInteger_ReturnsLong()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("je-inject-int", 0.0);

        // Inject a raw JsonElement integer
        var je = JsonSerializer.Deserialize<JsonElement>("42");
        InjectRawFlagDef(client, "je-inject-int", "test", je);

        var result = handle.Get();
        Assert.Equal(42.0, result);
    }

    [Fact]
    public void NumberFlag_JsonElementDouble_ReturnsDouble()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("je-inject-dbl", 0.0);

        // Inject a raw JsonElement double
        var je = JsonSerializer.Deserialize<JsonElement>("3.14");
        InjectRawFlagDef(client, "je-inject-dbl", "test", je);

        var result = handle.Get();
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void BooleanFlag_JsonElementNonBool_ReturnsDefault()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.BooleanFlag("je-inject-nonbool", true);

        // Inject a raw JsonElement that is NOT a boolean (number)
        var je = JsonSerializer.Deserialize<JsonElement>("42");
        InjectRawFlagDef(client, "je-inject-nonbool", "test", je);

        // JsonElement number is not bool, and not JsonElement True/False => fall to Default
        var result = handle.Get();
        Assert.True(result);
    }

    [Fact]
    public void StringFlag_JsonElementNonString_ReturnsDefault()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.StringFlag("je-inject-nonstr", "code-default");

        // Inject a raw JsonElement that is NOT a string (number)
        var je = JsonSerializer.Deserialize<JsonElement>("42");
        InjectRawFlagDef(client, "je-inject-nonstr", "test", je);

        // JsonElement number is not string => fall to Default
        var result = handle.Get();
        Assert.Equal("code-default", result);
    }

    [Fact]
    public void NumberFlag_JsonElementIntegerViaInjection_ReturnsNumber()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("je-inject-num-int", 0.0);

        // Inject a raw JsonElement integer
        var je = JsonSerializer.Deserialize<JsonElement>("55");
        InjectRawFlagDef(client, "je-inject-num-int", "test", je);

        var result = handle.Get();
        Assert.Equal(55.0, result);
    }

    [Fact]
    public void NumberFlag_JsonElementDoubleViaInjection_ReturnsNumber()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("je-inject-num-dbl", 0.0);

        // Inject a raw JsonElement double (has decimal point)
        var je = JsonSerializer.Deserialize<JsonElement>("7.77");
        InjectRawFlagDef(client, "je-inject-num-dbl", "test", je);

        var result = handle.Get();
        Assert.Equal(7.77, result);
    }

    [Fact]
    public void NumberFlag_JsonElementNull_ReturnsDefault()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("je-inject-num-null", 9.9);

        // Inject a raw JsonElement of type Null -- neither TryGetInt64 nor TryGetDouble succeeds
        var je = JsonSerializer.Deserialize<JsonElement>("null");
        InjectRawFlagDef(client, "je-inject-num-null", "test", je);

        var result = handle.Get();
        Assert.Equal(9.9, result);
    }
}
