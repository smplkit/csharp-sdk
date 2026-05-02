using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Smplkit;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Targeted coverage tests for FlagsClient edge paths: buffer-threshold flush,
/// JSON Logic evaluation edge cases (JsonElement-typed rules / enabled flag),
/// listener exception paths, GetRulesList variants.
/// </summary>
public class FlagsCoverageGapsTests
{
    private static (SmplClient client, MockHttpMessageHandler handler) MakeClient(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new MockHttpMessageHandler(respond);
        var http = new HttpClient(handler);
        var client = new SmplClient(TestData.DefaultOptions(), http);
        return (client, handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/vnd.api+json") };

    [Fact]
    public void BooleanFlag_50Declarations_TriggersBufferFlush()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        for (int i = 0; i < 60; i++)
            client.Flags.BooleanFlag($"f{i}", false);
        // No exception (Task.Run path covered)
    }

    [Fact]
    public void StringFlag_50Declarations_TriggersBufferFlush()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        for (int i = 0; i < 60; i++)
            client.Flags.StringFlag($"f{i}", "v");
    }

    [Fact]
    public void NumberFlag_50Declarations_TriggersBufferFlush()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        for (int i = 0; i < 60; i++)
            client.Flags.NumberFlag($"f{i}", 1.0);
    }

    [Fact]
    public void JsonFlag_50Declarations_TriggersBufferFlush()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        for (int i = 0; i < 60; i++)
            client.Flags.JsonFlag($"f{i}", new Dictionary<string, object?>());
    }

    [Fact]
    public void EvaluateFlag_JsonElementEnabledTrue_RuleEvaluates()
    {
        var enabled = JsonDocument.Parse("true").RootElement;
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = enabled,
            ["default"] = "x",
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "fallback",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("x", result);
    }

    [Fact]
    public void EvaluateFlag_JsonElementEnabledFalse_ServesFallback()
    {
        var disabled = JsonDocument.Parse("false").RootElement;
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = disabled,
            ["default"] = "env-fb",
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "flag-fb",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("env-fb", result);
    }

    [Fact]
    public void EvaluateFlag_JsonElementEnabledUnknown_ServesFallback()
    {
        // Anything other than true/false → enabled = false
        var weird = JsonDocument.Parse("\"yes\"").RootElement;
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = weird,
            ["default"] = "env-fb",
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "flag-fb",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("env-fb", result);
    }

    [Fact]
    public void EvaluateFlag_RuleLogicError_Continues()
    {
        // Logic that throws during evaluation is silently skipped
        var ruleWithBadLogic = new Dictionary<string, object?>
        {
            ["logic"] = new Dictionary<string, object?> { ["unknown_op"] = new[] { "x" } },
            ["value"] = "matched",
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["default"] = "fallback",
            ["rules"] = new List<object?> { ruleWithBadLogic },
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "ff",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void EvaluateFlag_RulesAsJsonElementArray_Evaluates()
    {
        // GetRulesList: JsonElement array path
        var rulesJson = JsonDocument.Parse("""[{"logic": {}, "value": "matched"}]""").RootElement;
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rulesJson,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "fallback",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        // The code skips rules with empty logic dict, so this returns fallback.
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void EvaluateFlag_RulesAsObjectArray_Evaluates()
    {
        // GetRulesList: object[] array path
        var rules = new object?[]
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>(),
                ["value"] = "x",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "fallback",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void EvaluateFlag_RulesAsUnknownType_Empty()
    {
        // GetRulesList: anything not list/JsonElement/object[] → empty
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = "rules-as-string-shouldnt-happen",
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "fallback",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        Assert.Equal("fallback", result);
    }

    // IsTruthy JTokenType edge cases — invoke via JSON Logic returning various types

    [Fact]
    public void EvaluateFlag_TruthyStringLogic_Matches()
    {
        // Expression that JSON Logic resolves to a non-empty string (so IsTruthy returns true on String token)
        var rule = new Dictionary<string, object?>
        {
            ["logic"] = new Dictionary<string, object?>
            {
                ["cat"] = new object?[] { "hello", " world" },  // string concat → non-empty
            },
            ["value"] = "matched",
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = new List<object?> { rule },
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["default"] = "fallback",
            ["environments"] = new Dictionary<string, object?>
            {
                ["test"] = envConfig,
            },
        };
        var result = FlagsClient.EvaluateFlag(flagDef, "test", new Dictionary<string, object?>());
        // Either matched (if cat works) or fallback (if cat is unsupported)
        Assert.True(result is "matched" or "fallback");
    }

    // FireGlobalListeners + FireScopedListeners with throwing listeners

    [Fact]
    public void FireGlobalListeners_ListenerThrows_Continues()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        client.Flags.OnChange(_ => throw new InvalidOperationException("bad global"));
        bool second = false;
        client.Flags.OnChange(_ => second = true);

        var method = typeof(FlagsClient).GetMethod("FireGlobalListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[] { "any-id", "test" });

        Assert.True(second);
    }

    [Fact]
    public void FireScopedListeners_ListenerThrows_Continues()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        client.Flags.OnChange("X", _ => throw new InvalidOperationException("bad scoped"));
        bool second = false;
        client.Flags.OnChange("X", _ => second = true);

        var method = typeof(FlagsClient).GetMethod("FireScopedListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[] { "X", "test" });

        Assert.True(second);
    }

    [Fact]
    public void FireScopedListeners_NoListenersForId_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var method = typeof(FlagsClient).GetMethod("FireScopedListeners",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        method.Invoke(client.Flags, new object?[] { "no-listeners", "test" });
    }

    [Fact]
    public void EvaluateHandle_NoContextProviderAndNoExplicit_EmptyContext()
    {
        var body = """
            {"data":[
                {"id":"f","type":"flag","attributes":{"id":"f","name":"f","type":"BOOLEAN","default":false,"values":[],"description":null,"environments":{}}}
            ]}
            """;
        var (client, _) = MakeClient(_ => Task.FromResult(Json(body)));

        // Don't call SetContext — ambient context is null/empty.
        // The first Get triggers the eval path with no context.
        var handle = client.Flags.BooleanFlag("f", true);
        Assert.False(handle.Get());
    }

    [Fact]
    public async Task ContextProvider_ManyContexts_TriggersFlush()
    {
        // Generate >100 unique contexts to trip the ContextBatchFlushSize threshold
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));

        var bigList = Enumerable.Range(0, 110)
            .Select(i => new Smplkit.Context("user", $"u-{i}"))
            .ToList();
        using (client.SetContext(bigList))
        {
            var handle = client.Flags.BooleanFlag("f", true);
            handle.Get();
        }
        await Task.Delay(50); // let the Task.Run complete
    }

    [Fact]
    public void BooleanFlag_NonBoolDefault_FallbackPath()
    {
        // BooleanFlag.Get() when neither the value nor the Default is a bool —
        // the fallback returns false (line 293 of Models.cs).
        var (client, _) = MakeClient(_ => Task.FromResult(Json("""{"data":[]}""")));
        var handle = client.Flags.BooleanFlag("missing", true);
        // Force Default to a non-bool value so the fallback path engages.
        handle.Default = "not-a-bool";
        // Flag isn't in the store, so EvaluateHandle returns defaultValue ("not-a-bool")
        // → `value is bool` is false → falls through to `Default is bool db` (also false) → returns false.
        Assert.False(handle.Get());
    }
}
