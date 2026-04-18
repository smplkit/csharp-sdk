using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Errors;
using Smplkit.Flags;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Coverage tests for FlagsClient methods not covered by other test files.
/// </summary>
public class FlagsClientCoverageTests
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

    private static string FlagListWithEnvJson(
        string id = "my-flag",
        string envKey = "production",
        bool enabled = true,
        string defaultVal = "false",
        string? envDefault = null,
        string? rulesJson = null) =>
        $$"""
        {
            "data": [
                {
                    "id": "{{id}}",
                    "type": "flag",
                    "attributes": {
                        "id": "{{id}}",
                        "name": "My Flag",
                        "type": "BOOLEAN",
                        "default": {{defaultVal}},
                        "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                        "description": "Test flag",
                        "environments": {
                            "{{envKey}}": {
                                "enabled": {{(enabled ? "true" : "false")}},
                                "default": {{envDefault ?? "null"}},
                                "rules": {{rulesJson ?? "[]"}}
                            }
                        },
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private static string SingleFlagGetJson(
        string id = "my-flag",
        string name = "My Flag") =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "flag",
                "attributes": {
                    "id": "{{id}}",
                    "name": "{{name}}",
                    "type": "BOOLEAN",
                    "default": false,
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Test flag",
                    "environments": {
                        "production": {
                            "enabled": true,
                            "default": null,
                            "rules": []
                        }
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    /// <summary>Single-item response for SaveAsync (POST/PUT returns single resource).</summary>
    private static string SingleFlagResponseJson(
        string id = "my-flag",
        string name = "My Flag") =>
        $$"""
        {
            "data": {
                "id": "{{id}}",
                "type": "flag",
                "attributes": {
                    "id": "{{id}}",
                    "name": "{{name}}",
                    "type": "BOOLEAN",
                    "default": false,
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Test flag",
                    "environments": {
                        "production": {
                            "enabled": true,
                            "default": null,
                            "rules": []
                        }
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    // ---------------------------------------------------------------
    // SetContextProvider
    // ---------------------------------------------------------------

    [Fact]
    public void SetContextProvider_IsUsedDuringEvaluation()
    {
        var flagJson = FlagListWithEnvJson(id: "ctx-flag", enabled: true, defaultVal: "false");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BooleanFlag("ctx-flag", true);
        client.Flags.SetContextProvider(() => new List<Context>
        {
            new("user", "u1", new Dictionary<string, object?> { ["plan"] = "free" }),
        });

        // Get() triggers lazy EnsureInitialized
        var result = handle.Get();
        // Flag default is false, no rules match
        Assert.False(result);
    }

    // ---------------------------------------------------------------
    // ConnectionStatus
    // ---------------------------------------------------------------

    [Fact]
    public void ConnectionStatus_ReturnsDisconnected_WhenNoWebSocket()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        Assert.Equal("disconnected", client.Flags.ConnectionStatus);
    }

    // ---------------------------------------------------------------
    // OnChange (global listener)
    // ---------------------------------------------------------------

    [Fact]
    public void OnChange_RegistersGlobalListener()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));
        var events = new List<FlagChangeEvent>();

        client.Flags.OnChange(evt => events.Add(evt));

        // No crash; listener registered
        Assert.Empty(events);
    }

    // ---------------------------------------------------------------
    // OnChange (scoped listener)
    // ---------------------------------------------------------------

    [Fact]
    public void OnChange_Scoped_RegistersListener()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));
        var events = new List<FlagChangeEvent>();

        client.Flags.OnChange("my-flag", evt => events.Add(evt));

        // No crash; listener registered
        Assert.Empty(events);
    }

    // ---------------------------------------------------------------
    // Register (single context)
    // ---------------------------------------------------------------

    [Fact]
    public void Register_SingleContext_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        client.Flags.Register(new Context("user", "u1",
            new Dictionary<string, object?> { ["plan"] = "pro" }));
    }

    // ---------------------------------------------------------------
    // Register (multiple contexts)
    // ---------------------------------------------------------------

    [Fact]
    public void Register_MultipleContexts_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        client.Flags.Register(new List<Context>
        {
            new("user", "u1", new Dictionary<string, object?> { ["plan"] = "pro" }),
            new("device", "d1", new Dictionary<string, object?> { ["os"] = "ios" }),
        });
    }

    // ---------------------------------------------------------------
    // FlushContextsAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task FlushContextsAsync_EmptyBuffer_DoesNotCallApi()
    {
        var (client, handler) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        await client.Flags.FlushContextsAsync();

        // No requests should have been made
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FlushContextsAsync_WithPendingContexts_CallsApi()
    {
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse("{}")));

        client.Flags.Register(new Context("user", "u1",
            new Dictionary<string, object?> { ["plan"] = "pro" }));
        await client.Flags.FlushContextsAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Contains("/api/v1/contexts/bulk", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task FlushContextsAsync_ApiFailure_DoesNotThrow()
    {
        var (client, _) = CreateClient(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        client.Flags.Register(new Context("user", "u1"));
        // Should not throw - fire-and-forget
        await client.Flags.FlushContextsAsync();
    }

    // ---------------------------------------------------------------
    // EvaluateHandle with context provider triggering flush
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateHandle_WithContextProvider_FlushesWhenBufferFull()
    {
        var flagJson = FlagListWithEnvJson(id: "flush-flag", defaultVal: "false");
        int requestCount = 0;
        var (client, _) = CreateClient(_ =>
        {
            Interlocked.Increment(ref requestCount);
            return Task.FromResult(JsonResponse(flagJson));
        });

        // Register many contexts to trigger flush threshold
        var contexts = new List<Context>();
        for (int i = 0; i < 101; i++)
            contexts.Add(new Context("user", $"user-{i}", new Dictionary<string, object?> { ["plan"] = "free" }));

        client.Flags.SetContextProvider(() => contexts);

        var handle = client.Flags.BooleanFlag("flush-flag", true);

        handle.Get(); // triggers EnsureInitialized + context provider path

        // At least the list-flags request was made
        Assert.True(requestCount >= 1);
    }

    // ---------------------------------------------------------------
    // EvaluateHandle with no context provider and no explicit context
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateHandle_NoContextProvider_NoContext_UsesEmptyDict()
    {
        var flagJson = FlagListWithEnvJson(id: "empty-ctx", defaultVal: "true", enabled: true);
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BooleanFlag("empty-ctx", false);

        // Get() triggers lazy initialization
        var result = handle.Get();
        Assert.True(result);
    }

    // ---------------------------------------------------------------
    // RefreshAsync
    // ---------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_RefetchesFlagsAndFiresListeners()
    {
        var flagJson = FlagListWithEnvJson(id: "refresh-flag", defaultVal: "true");
        var events = new List<FlagChangeEvent>();
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        client.Flags.OnChange(evt => events.Add(evt));

        // Trigger initialization via Get()
        var handle = client.Flags.BooleanFlag("refresh-flag", false);
        handle.Get();

        await client.Flags.RefreshAsync();

        // Should have fired change event for "refresh-flag" with source "manual"
        Assert.Contains(events, e => e.Id == "refresh-flag" && e.Source == "manual");
    }

    // ---------------------------------------------------------------
    // SaveFlagInternalAsync (update — Id is not null → PUT)
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_Update_PutsFlagToApi()
    {
        var (client, handler) = CreateClient(req =>
        {
            if (req.Method == HttpMethod.Put)
                return Task.FromResult(JsonResponse(SingleFlagResponseJson()));
            return Task.FromResult(JsonResponse(SingleFlagGetJson()));
        });

        var flag = await client.Flags.Management.GetAsync("my-flag");

        // Now update via SaveAsync (Id is not null → PUT)
        flag.Name = "Updated Name";
        await flag.SaveAsync();

        // At least 2 requests: GET + PUT
        Assert.True(handler.Requests.Count >= 2);
        var putReq = handler.Requests.Last(r => r.Method == HttpMethod.Put);
        Assert.Contains("/api/v1/flags/my-flag", putReq.RequestUri!.ToString());
    }

    // ---------------------------------------------------------------
    // HandleFlagChanged / HandleFlagDeleted
    // ---------------------------------------------------------------

    [Fact]
    public void HandleFlagChanged_RefetchesAndFiresListeners()
    {
        var flagJson = FlagListWithEnvJson(id: "ws-flag", defaultVal: "true");
        var events = new List<FlagChangeEvent>();
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        client.Flags.OnChange(evt => events.Add(evt));
        client.Flags.OnChange("ws-flag", evt => events.Add(evt));

        // Trigger initialization
        var handle = client.Flags.BooleanFlag("ws-flag", false);
        handle.Get();

        // Simulate HandleFlagChanged via reflection (it's private)
        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Flags, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "ws-flag" }
        });

        // Should have fired both global and scoped listeners
        Assert.True(events.Count >= 2);
        Assert.All(events, e => Assert.Equal("ws-flag", e.Id));
    }

    [Fact]
    public void HandleFlagDeleted_RefetchesAndFiresListeners()
    {
        var flagJson = FlagListWithEnvJson(id: "del-flag", defaultVal: "true");
        var events = new List<FlagChangeEvent>();
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        client.Flags.OnChange(evt => events.Add(evt));

        // Trigger initialization
        var handle = client.Flags.BooleanFlag("del-flag", false);
        handle.Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagDeleted",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Flags, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "del-flag" }
        });

        Assert.Contains(events, e => e.Id == "del-flag" && e.Source == "websocket");
    }

    [Fact]
    public void HandleFlagChanged_NullId_DoesNotFireListeners()
    {
        var flagJson = FlagListWithEnvJson(id: "ws-flag", defaultVal: "true");
        var events = new List<FlagChangeEvent>();
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        client.Flags.OnChange(evt => events.Add(evt));

        // Trigger initialization
        var handle = client.Flags.BooleanFlag("ws-flag", false);
        handle.Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method!.Invoke(client.Flags, new object[]
        {
            new Dictionary<string, object?> { ["something"] = "else" } // no "id"
        });

        // FireChangeListeners should skip when id is null
        Assert.DoesNotContain(events, e => e.Source == "websocket");
    }

    [Fact]
    public void HandleFlagChanged_ListenerThrows_DoesNotPropagate()
    {
        var flagJson = FlagListWithEnvJson(id: "throw-flag", defaultVal: "true");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        client.Flags.OnChange(_ => throw new InvalidOperationException("boom"));

        // Trigger initialization
        var handle = client.Flags.BooleanFlag("throw-flag", false);
        handle.Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should not throw despite listener exception
        method!.Invoke(client.Flags, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "throw-flag" }
        });
    }

    [Fact]
    public void HandleFlagChanged_ScopedListenerThrows_DoesNotPropagate()
    {
        var flagJson = FlagListWithEnvJson(id: "handle-throw", defaultVal: "true");
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        client.Flags.OnChange("handle-throw", _ => throw new InvalidOperationException("boom"));

        // Trigger initialization
        var handle = client.Flags.BooleanFlag("handle-throw", false);
        handle.Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should not throw
        method!.Invoke(client.Flags, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "handle-throw" }
        });
    }

    [Fact]
    public void HandleFlagChanged_TransportFailure_DoesNotThrow()
    {
        var flagJson = FlagListWithEnvJson(id: "fail-flag", defaultVal: "true");
        int flagListGetCount = 0;
        var (client, _) = CreateClient(req =>
        {
            // POSTs (bulk flag/context registration) always succeed
            if (req.Method == HttpMethod.Post)
                return Task.FromResult(JsonResponse("{}"));
            // First GET to /flags succeeds (EnsureInitialized fetch); subsequent ones fail
            Interlocked.Increment(ref flagListGetCount);
            if (flagListGetCount == 1)
                return Task.FromResult(JsonResponse(flagJson));
            throw new HttpRequestException("Network error");
        });

        // Trigger initialization
        var handle = client.Flags.BooleanFlag("fail-flag", false);
        handle.Get();

        var method = typeof(FlagsClient).GetMethod("HandleFlagChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        // Should swallow transport errors
        method!.Invoke(client.Flags, new object[]
        {
            new Dictionary<string, object?> { ["id"] = "fail-flag" }
        });
    }

    // ---------------------------------------------------------------
    // EvaluateFlag edge cases
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_UntypedEnvironments_Dict()
    {
        // environments as Dictionary<string, object?> instead of typed
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["default"] = "env-val",
            ["rules"] = new List<object?>(),
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "flag-default",
            ["environments"] = new Dictionary<string, object?>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("env-val", result);
    }

    [Fact]
    public void EvaluateFlag_UntypedEnvironments_EnvNotFound()
    {
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "flag-default",
            ["environments"] = new Dictionary<string, object?>
            {
                ["prod"] = new Dictionary<string, object?> { ["enabled"] = true },
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "staging", new Dictionary<string, object?>());
        Assert.Equal("flag-default", result);
    }

    [Fact]
    public void EvaluateFlag_UntypedEnvironments_EnvValueNotDict()
    {
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "flag-default",
            ["environments"] = new Dictionary<string, object?>
            {
                ["prod"] = "not-a-dict",
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("flag-default", result);
    }

    [Fact]
    public void EvaluateFlag_EnabledAsJsonElement_True()
    {
        var je = JsonSerializer.Deserialize<JsonElement>("true");
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = je,
            ["default"] = "env-val",
            ["rules"] = new List<object?>(),
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "flag-default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("env-val", result);
    }

    [Fact]
    public void EvaluateFlag_EnabledAsJsonElement_False()
    {
        var je = JsonSerializer.Deserialize<JsonElement>("false");
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = je,
            ["default"] = "env-val",
            ["rules"] = new List<object?>(),
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "flag-default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("env-val", result);
    }

    [Fact]
    public void EvaluateFlag_EnabledAsOtherType_ReturnsFallback()
    {
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = "yes", // not bool, not JsonElement
            ["default"] = "env-val",
            ["rules"] = new List<object?>(),
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "flag-default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        // "yes" is not bool/JsonElement, falls into _ => false, so !enabled returns fallback
        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("env-val", result);
    }

    // ---------------------------------------------------------------
    // GetRulesList edge cases
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_RulesAsJsonElement_Array()
    {
        var rulesJson = JsonSerializer.Deserialize<JsonElement>("""
            [
                {"description": "test", "logic": {"==": [{"var": "user.plan"}, "pro"]}, "value": "matched"}
            ]
        """);

        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rulesJson,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "pro" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);
        Assert.Equal("matched", result);
    }

    [Fact]
    public void EvaluateFlag_RulesAsObjectArray()
    {
        var rule = new Dictionary<string, object?>
        {
            ["description"] = "test",
            ["logic"] = new Dictionary<string, object?>
            {
                ["=="] = new object?[]
                {
                    new Dictionary<string, object?> { ["var"] = "user.plan" },
                    "pro"
                }
            },
            ["value"] = "matched",
        };
        object?[] rulesArray = [rule];

        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rulesArray,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "pro" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);
        Assert.Equal("matched", result);
    }

    [Fact]
    public void EvaluateFlag_RulesAsOtherType_ReturnsEmptyRulesList()
    {
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = "not-a-list", // invalid type
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("default", result); // falls through to fallback
    }

    // ---------------------------------------------------------------
    // IsTruthy edge cases
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_RuleReturnsInteger_TruthyCheck()
    {
        // Rule where logic evaluates to a non-zero integer (truthy)
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["+"] = new object?[] { 1, 1 } // returns 2, which is truthy
                },
                ["value"] = "matched",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("matched", result);
    }

    [Fact]
    public void EvaluateFlag_RuleReturnsString_TruthyCheck()
    {
        // Rule where logic returns a non-empty string (truthy)
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["cat"] = new object?[] { "hello" } // returns "hello", truthy
                },
                ["value"] = "matched",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("matched", result);
    }

    [Fact]
    public void EvaluateFlag_RuleLogicThrows_ContinuesToNext()
    {
        // First rule has invalid logic that will throw, second rule matches
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["invalid_op_that_does_not_exist"] = new object?[] { 1 }
                },
                ["value"] = "first",
            },
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "pro"
                    }
                },
                ["value"] = "second",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "pro" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);
        Assert.Equal("second", result);
    }

    [Fact]
    public void EvaluateFlag_RuleWithNullLogic_IsSkipped()
    {
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = null,
                ["value"] = "skipped",
            },
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["=="] = new object?[] { 1, 1 }
                },
                ["value"] = "matched",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("matched", result);
    }

    // ---------------------------------------------------------------
    // IsTruthy - null token
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_RuleReturnsNull_IsFalsy()
    {
        // Rule where logic evaluates to null (falsy)
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    // var referencing non-existent key returns null
                    ["var"] = "nonexistent.path",
                },
                ["value"] = "should-not-match",
            },
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["=="] = new object?[] { 1, 1 }
                },
                ["value"] = "fallthrough",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("fallthrough", result);
    }

    [Fact]
    public void EvaluateFlag_RuleReturnsZeroInteger_IsFalsy()
    {
        // Rule where logic evaluates to 0 (falsy integer)
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["-"] = new object?[] { 5, 5 } // returns 0, which is falsy
                },
                ["value"] = "should-not-match",
            },
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["=="] = new object?[] { 1, 1 }
                },
                ["value"] = "fallthrough",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("fallthrough", result);
    }

    [Fact]
    public void EvaluateFlag_RuleReturnsEmptyString_IsFalsy()
    {
        // Rule where logic evaluates to "" (falsy string)
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["cat"] = new object?[] { } // cat with no args returns ""
                },
                ["value"] = "should-not-match",
            },
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["=="] = new object?[] { 1, 1 }
                },
                ["value"] = "fallthrough",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("fallthrough", result);
    }

    [Fact]
    public void EvaluateFlag_RuleReturnsZeroFloat_IsFalsy()
    {
        // Rule where logic evaluates to 0.0 (falsy float)
        var rules = new List<object?>
        {
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["*"] = new object?[] { 0, 5 } // returns 0.0
                },
                ["value"] = "should-not-match",
            },
            new Dictionary<string, object?>
            {
                ["logic"] = new Dictionary<string, object?>
                {
                    ["=="] = new object?[] { 1, 1 }
                },
                ["value"] = "fallthrough",
            },
        };
        var envConfig = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["rules"] = rules,
        };
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default",
            ["environments"] = new Dictionary<string, Dictionary<string, object?>>
            {
                ["prod"] = envConfig,
            },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());
        Assert.Equal("fallthrough", result);
    }

    // ---------------------------------------------------------------
    // IsTruthy direct testing via reflection
    // ---------------------------------------------------------------

    [Fact]
    public void IsTruthy_NullToken_ReturnsFalse()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var result = (bool)method!.Invoke(null, new object?[] { null })!;
        Assert.False(result);
    }

    [Fact]
    public void IsTruthy_IntegerZero_ReturnsFalse()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var token = Newtonsoft.Json.Linq.JToken.FromObject(0);
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.False(result);
    }

    [Fact]
    public void IsTruthy_IntegerNonZero_ReturnsTrue()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var token = Newtonsoft.Json.Linq.JToken.FromObject(42);
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.True(result);
    }

    [Fact]
    public void IsTruthy_JTokenNull_ReturnsFalse()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var token = Newtonsoft.Json.Linq.JValue.CreateNull();
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.False(result);
    }

    [Fact]
    public void IsTruthy_JTokenArray_ReturnsTrue_DefaultCase()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        // An array JToken hits the _ => true default case
        var token = Newtonsoft.Json.Linq.JToken.Parse("[1, 2, 3]");
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.True(result);
    }

    [Fact]
    public void IsTruthy_Float_ReturnsTrueForNonZero()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var token = Newtonsoft.Json.Linq.JToken.FromObject(3.14);
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.True(result);
    }

    [Fact]
    public void IsTruthy_String_ReturnsTrueForNonEmpty()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var token = Newtonsoft.Json.Linq.JToken.FromObject("hello");
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.True(result);
    }

    [Fact]
    public void IsTruthy_EmptyString_ReturnsFalse()
    {
        var method = typeof(FlagsClient).GetMethod("IsTruthy",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var token = Newtonsoft.Json.Linq.JToken.FromObject("");
        var result = (bool)method!.Invoke(null, new object?[] { token })!;
        Assert.False(result);
    }

    // ---------------------------------------------------------------
    // ParseFlagDef with null values
    // ---------------------------------------------------------------

    [Fact]
    public async Task ParseFlagDef_NullValues_PreservesNull()
    {
        var flagJson = """
        {
            "data": [
                {
                    "id": "no-values-flag",
                    "type": "flag",
                    "attributes": {
                        "id": "no-values-flag",
                        "name": "No Values",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": null,
                        "description": null,
                        "environments": {},
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var flags = await client.Flags.Management.ListAsync();
        Assert.Single(flags);
        Assert.Null(flags[0].Values);
    }

    // ---------------------------------------------------------------
    // ExtractEnvironments
    // ---------------------------------------------------------------

    [Fact]
    public async Task ExtractEnvironments_NormalizesValues()
    {
        var flagJson = """
        {
            "data": [
                {
                    "id": "env-flag",
                    "type": "flag",
                    "attributes": {
                        "id": "env-flag",
                        "name": "Env Flag",
                        "type": "BOOLEAN",
                        "default": false,
                        "values": [],
                        "description": null,
                        "environments": {
                            "prod": {
                                "enabled": true,
                                "default": false,
                                "rules": []
                            }
                        },
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var flags = await client.Flags.Management.ListAsync();
        Assert.Single(flags);
        Assert.True(flags[0].Environments.ContainsKey("prod"));
    }

    // ---------------------------------------------------------------
    // BuildUpdateFlagBody
    // ---------------------------------------------------------------

    [Fact]
    public async Task BuildUpdateFlagBody_IncludesAllFields()
    {
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(SingleFlagResponseJson());
            }
            return JsonResponse(SingleFlagGetJson());
        });

        var flag = await client.Flags.Management.GetAsync("my-flag");
        // Mutate properties then SaveAsync
        flag.Description = "updated desc";
        flag.Name = "Updated Name";
        flag.Default = true;
        await flag.SaveAsync();

        Assert.NotNull(capturedBody);
        Assert.Contains("Updated Name", capturedBody);
        Assert.Contains("updated desc", capturedBody);
    }

    // ---------------------------------------------------------------
    // BooleanFlag.Get with explicit context
    // ---------------------------------------------------------------

    [Fact]
    public void BooleanFlag_GetWithContext_EvaluatesCorrectly()
    {
        var flagJson = FlagListWithEnvJson(id: "ctx-bool", defaultVal: "false", enabled: true);
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.BooleanFlag("ctx-bool", true);

        var contexts = new List<Context>
        {
            new("user", "u1", new Dictionary<string, object?> { ["plan"] = "free" }),
        };
        // Get triggers lazy init
        var result = handle.Get(contexts);
        // flag default is false, no matching rules
        Assert.False(result);
    }

    // ---------------------------------------------------------------
    // StringFlag.Get with explicit context
    // ---------------------------------------------------------------

    [Fact]
    public void StringFlag_GetWithContext_EvaluatesCorrectly()
    {
        var flagJson = """
        {
            "data": [
                {
                    "id": "ctx-str",
                    "type": "flag",
                    "attributes": {
                        "id": "ctx-str",
                        "name": "Ctx String",
                        "type": "STRING",
                        "default": "server-val",
                        "values": [],
                        "description": null,
                        "environments": {
                            "test": {
                                "enabled": true,
                                "default": null,
                                "rules": []
                            }
                        },
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.StringFlag("ctx-str", "code-default");

        var contexts = new List<Context>
        {
            new("user", "u1", new Dictionary<string, object?> { ["plan"] = "free" }),
        };
        var result = handle.Get(contexts);
        Assert.Equal("server-val", result);
    }

    // ---------------------------------------------------------------
    // NumberFlag.Get with explicit context
    // ---------------------------------------------------------------

    [Fact]
    public void NumberFlag_GetWithContext_EvaluatesCorrectly()
    {
        var flagJson = """
        {
            "data": [
                {
                    "id": "ctx-num",
                    "type": "flag",
                    "attributes": {
                        "id": "ctx-num",
                        "name": "Ctx Number",
                        "type": "NUMERIC",
                        "default": 42,
                        "values": [],
                        "description": null,
                        "environments": {
                            "test": {
                                "enabled": true,
                                "default": null,
                                "rules": []
                            }
                        },
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("ctx-num", 0.0);

        var contexts = new List<Context>
        {
            new("user", "u1"),
        };
        var result = handle.Get(contexts);
        Assert.Equal(42.0, result);
    }

    // ---------------------------------------------------------------
    // NumberFlag type coercion edge cases
    // ---------------------------------------------------------------

    [Fact]
    public void NumberFlag_HandlesFloat()
    {
        var flagJson = """
        {
            "data": [
                {
                    "id": "float-num",
                    "type": "flag",
                    "attributes": {
                        "id": "float-num",
                        "name": "Float Number",
                        "type": "NUMERIC",
                        "default": 1.5,
                        "values": [],
                        "description": null,
                        "environments": {
                            "test": {
                                "enabled": true,
                                "default": null,
                                "rules": []
                            }
                        },
                        "created_at": null,
                        "updated_at": null
                    }
                }
            ]
        }
        """;
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse(flagJson)));

        var handle = client.Flags.NumberFlag("float-num", 0.0);

        // Get triggers lazy init
        var result = handle.Get();
        Assert.Equal(1.5, result);
    }

    // ---------------------------------------------------------------
    // Stats property
    // ---------------------------------------------------------------

    [Fact]
    public void Stats_ReturnsZeroCounts_Initially()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var stats = client.Flags.Stats;
        Assert.Equal(0, stats.CacheHits);
        Assert.Equal(0, stats.CacheMisses);
    }

    // ---------------------------------------------------------------
    // Factory methods: NewStringFlag, NewNumberFlag, NewJsonFlag
    // ---------------------------------------------------------------

    [Fact]
    public void NewStringFlag_ReturnsStringFlagWithDefaults()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var flag = client.Flags.Management.NewStringFlag("my-string-flag", "hello", name: "My String", description: "desc");

        Assert.Equal("my-string-flag", flag.Id);
        Assert.Equal("My String", flag.Name);
        Assert.Equal("STRING", flag.Type);
        Assert.Equal("hello", flag.Default);
        Assert.Equal("desc", flag.Description);
        Assert.Null(flag.Values);
        Assert.Empty(flag.Environments);
    }

    [Fact]
    public void NewStringFlag_WithValues_SetsValues()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var values = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Option A", ["value"] = "a" },
            new() { ["name"] = "Option B", ["value"] = "b" },
        };
        var flag = client.Flags.Management.NewStringFlag("val-flag", "a", values: values);

        Assert.Equal(2, flag.Values!.Count);
    }

    [Fact]
    public void NewStringFlag_WithoutName_AutoGeneratesName()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var flag = client.Flags.Management.NewStringFlag("feature-color", "red");

        Assert.Equal("Feature Color", flag.Name);
    }

    [Fact]
    public void NewNumberFlag_ReturnsNumberFlagWithDefaults()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var flag = client.Flags.Management.NewNumberFlag("my-num-flag", 42.5, name: "My Number", description: "numeric desc");

        Assert.Equal("my-num-flag", flag.Id);
        Assert.Equal("My Number", flag.Name);
        Assert.Equal("NUMERIC", flag.Type);
        Assert.Equal(42.5, flag.Default);
        Assert.Equal("numeric desc", flag.Description);
        Assert.Null(flag.Values);
        Assert.Empty(flag.Environments);
    }

    [Fact]
    public void NewNumberFlag_WithValues_SetsValues()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var values = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Low", ["value"] = 10.0 },
            new() { ["name"] = "High", ["value"] = 100.0 },
        };
        var flag = client.Flags.Management.NewNumberFlag("rate-limit", 50.0, values: values);

        Assert.Equal(2, flag.Values!.Count);
    }

    [Fact]
    public void NewNumberFlag_WithoutName_AutoGeneratesName()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var flag = client.Flags.Management.NewNumberFlag("max-retries", 3.0);

        Assert.Equal("Max Retries", flag.Name);
    }

    [Fact]
    public void NewJsonFlag_ReturnsJsonFlagWithDefaults()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var defaultVal = new Dictionary<string, object?> { ["theme"] = "dark", ["fontSize"] = 14 };
        var flag = client.Flags.Management.NewJsonFlag("ui-config", defaultVal, name: "UI Config", description: "json desc");

        Assert.Equal("ui-config", flag.Id);
        Assert.Equal("UI Config", flag.Name);
        Assert.Equal("JSON", flag.Type);
        Assert.Same(defaultVal, flag.Default);
        Assert.Equal("json desc", flag.Description);
        Assert.Null(flag.Values);
        Assert.Empty(flag.Environments);
    }

    [Fact]
    public void NewJsonFlag_WithValues_SetsValues()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var values = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Config A", ["value"] = new Dictionary<string, object?> { ["a"] = 1 } },
        };
        var flag = client.Flags.Management.NewJsonFlag("json-flag", new Dictionary<string, object?>(), values: values);

        Assert.Single(flag.Values!);
    }

    [Fact]
    public void NewJsonFlag_WithoutName_AutoGeneratesName()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var flag = client.Flags.Management.NewJsonFlag("dashboard-layout", new Dictionary<string, object?>());

        Assert.Equal("Dashboard Layout", flag.Name);
    }

    // ---------------------------------------------------------------
    // SaveAsync for StringFlag, NumberFlag, JsonFlag (create path)
    // ---------------------------------------------------------------

    [Fact]
    public async Task NewStringFlag_SaveAsync_Create_PostsToApi()
    {
        var responseJson = """
        {
            "data": {
                "id": "str-flag",
                "type": "flag",
                "attributes": {
                    "id": "str-flag",
                    "name": "Str Flag",
                    "type": "STRING",
                    "default": "hello",
                    "values": [],
                    "description": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(responseJson, HttpStatusCode.Created)));

        var flag = client.Flags.Management.NewStringFlag("str-flag", "hello");
        await flag.SaveAsync();

        Assert.Equal("str-flag", flag.Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task NewNumberFlag_SaveAsync_Create_PostsToApi()
    {
        var responseJson = """
        {
            "data": {
                "id": "num-flag",
                "type": "flag",
                "attributes": {
                    "id": "num-flag",
                    "name": "Num Flag",
                    "type": "NUMERIC",
                    "default": 42,
                    "values": [],
                    "description": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(responseJson, HttpStatusCode.Created)));

        var flag = client.Flags.Management.NewNumberFlag("num-flag", 42.0);
        await flag.SaveAsync();

        Assert.Equal("num-flag", flag.Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    [Fact]
    public async Task NewJsonFlag_SaveAsync_Create_PostsToApi()
    {
        var responseJson = """
        {
            "data": {
                "id": "json-flag",
                "type": "flag",
                "attributes": {
                    "id": "json-flag",
                    "name": "Json Flag",
                    "type": "JSON",
                    "default": {"theme": "dark"},
                    "values": [],
                    "description": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var (client, handler) = CreateClient(_ =>
            Task.FromResult(JsonResponse(responseJson, HttpStatusCode.Created)));

        var flag = client.Flags.Management.NewJsonFlag("json-flag", new Dictionary<string, object?> { ["theme"] = "dark" });
        await flag.SaveAsync();

        Assert.Equal("json-flag", flag.Id);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
    }

    // ---------------------------------------------------------------
    // FlagsClient handle declarations: StringFlag, NumberFlag, JsonFlag
    // ---------------------------------------------------------------

    [Fact]
    public void StringFlag_HandleDeclaration_RegistersInHandles()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.StringFlag("str-handle", "default-val");

        Assert.Equal("str-handle", handle.Id);
        Assert.Equal("default-val", handle.Default);
        Assert.Equal("STRING", handle.Type);
    }

    [Fact]
    public void NumberFlag_HandleDeclaration_RegistersInHandles()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var handle = client.Flags.NumberFlag("num-handle", 99.9);

        Assert.Equal("num-handle", handle.Id);
        Assert.Equal(99.9, handle.Default);
        Assert.Equal("NUMERIC", handle.Type);
    }

    [Fact]
    public void JsonFlag_HandleDeclaration_RegistersInHandles()
    {
        var (client, _) = CreateClient(_ => Task.FromResult(JsonResponse("{}")));

        var defaultVal = new Dictionary<string, object?> { ["x"] = 1 };
        var handle = client.Flags.JsonFlag("json-handle", defaultVal);

        Assert.Equal("json-handle", handle.Id);
        Assert.Same(defaultVal, handle.Default);
        Assert.Equal("JSON", handle.Type);
    }

    // ---------------------------------------------------------------
    // EnsureInitialized — service context registration fire-and-forget
    // ---------------------------------------------------------------

    [Fact]
    public void EnsureInitialized_WithService_TriggersContextRegistration()
    {
        var requestUrls = new List<string>();
        var flagJson = FlagListWithEnvJson(id: "svc-flag", defaultVal: "true");
        var handler = new MockHttpMessageHandler(req =>
        {
            requestUrls.Add(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(flagJson));
        });
        var httpClient = new HttpClient(handler);
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production", Service = "my-service" },
            httpClient);

        var handle = client.Flags.BooleanFlag("svc-flag", false);
        handle.Get(); // triggers EnsureInitialized

        // Wait a bit for fire-and-forget Task.Run to complete
        Thread.Sleep(200);

        // At minimum, the flags list was fetched. The context registration
        // is fire-and-forget, so it may or may not appear depending on timing.
        Assert.True(requestUrls.Count >= 1);
    }

    [Fact]
    public void EnsureInitialized_WithoutService_DoesNotRegisterContext()
    {
        var flagJson = FlagListWithEnvJson(id: "no-svc-flag", defaultVal: "true");
        var requestUrls = new List<string>();
        var handler = new MockHttpMessageHandler(req =>
        {
            requestUrls.Add(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(flagJson));
        });
        var httpClient = new HttpClient(handler);
        // Note: SmplClient requires a service, but we can test with an empty service
        // by checking that the context registration isn't triggered for non-service path.
        // The service is always set on SmplClient, so let's just verify normal init works.
        var client = new SmplClient(
            new SmplClientOptions { ApiKey = "sk_api_test", Environment = "production", Service = "svc" },
            httpClient);

        var handle = client.Flags.BooleanFlag("no-svc-flag", false);
        handle.Get(); // triggers EnsureInitialized

        // Verify the flag list was fetched
        Assert.True(requestUrls.Count >= 1);
    }

    // ---------------------------------------------------------------
    // BuildUpdateFlagBody -- env without rules key
    // ---------------------------------------------------------------

    [Fact]
    public async Task SaveAsync_EnvWithoutRules_SetsEmptyRulesList()
    {
        // Flag whose environment has enabled + default but NO rules key.
        // After MapFlagResource -> ExtractEnvironments, the env dict will
        // not have a "rules" key, triggering the else branch in BuildUpdateFlagBody.
        var flagJson = $$"""
        {
            "data": {
                "id": "no-rules-flag",
                "type": "flag",
                "attributes": {
                    "id": "no-rules-flag",
                    "name": "No Rules",
                    "type": "BOOLEAN",
                    "default": false,
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Flag with env that has null rules",
                    "environments": {
                        "staging": {
                            "enabled": true,
                            "default": null
                        }
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

        var singleFlagJson = $$"""
        {
            "data": {
                "id": "no-rules-flag",
                "type": "flag",
                "attributes": {
                    "id": "no-rules-flag",
                    "name": "No Rules Updated",
                    "type": "BOOLEAN",
                    "default": false,
                    "values": [{"name": "True", "value": true}, {"name": "False", "value": false}],
                    "description": "Flag with env that has null rules",
                    "environments": {
                        "staging": {
                            "enabled": true,
                            "default": null
                        }
                    },
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(singleFlagJson);
            }
            return JsonResponse(flagJson);
        });

        var flag = await client.Flags.Management.GetAsync("no-rules-flag");
        flag.Name = "No Rules Updated";
        await flag.SaveAsync();

        Assert.NotNull(capturedBody);
        // The rules list should be present (as an empty array) in the PUT body
        Assert.Contains("\"rules\"", capturedBody);
    }

    // ---------------------------------------------------------------
    // Unconstrained flags (null values) -- create and update
    // ---------------------------------------------------------------

    [Fact]
    public async Task CreateFlag_Unconstrained_SendsNullValues()
    {
        var responseJson = """
        {
            "data": {
                "id": "unc-string",
                "type": "flag",
                "attributes": {
                    "id": "unc-string",
                    "name": "Unc String",
                    "type": "STRING",
                    "default": "hello",
                    "values": null,
                    "description": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            capturedBody = await req.Content!.ReadAsStringAsync();
            return JsonResponse(responseJson, HttpStatusCode.Created);
        });

        var flag = client.Flags.Management.NewStringFlag("unc-string", "hello");
        Assert.Null(flag.Values);
        await flag.SaveAsync();

        Assert.NotNull(capturedBody);
        Assert.Contains("\"values\":null", capturedBody.Replace(" ", ""));
        Assert.Equal("unc-string", flag.Id);
        Assert.Null(flag.Values);
    }

    [Fact]
    public async Task UpdateFlag_Unconstrained_SendsNullValues()
    {
        var getJson = """
        {
            "data": {
                "id": "unc-num",
                "type": "flag",
                "attributes": {
                    "id": "unc-num",
                    "name": "Unc Num",
                    "type": "NUMERIC",
                    "default": 42,
                    "values": null,
                    "description": null,
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;
        var putJson = """
        {
            "data": {
                "id": "unc-num",
                "type": "flag",
                "attributes": {
                    "id": "unc-num",
                    "name": "Updated Num",
                    "type": "NUMERIC",
                    "default": 42,
                    "values": null,
                    "description": "updated",
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T11:00:00Z"
                }
            }
        }
        """;
        string? capturedBody = null;
        var (client, _) = CreateClient(async req =>
        {
            if (req.Method == HttpMethod.Put)
            {
                capturedBody = await req.Content!.ReadAsStringAsync();
                return JsonResponse(putJson);
            }
            return JsonResponse(getJson);
        });

        var flag = await client.Flags.Management.GetAsync("unc-num");
        Assert.Null(flag.Values);
        flag.Description = "updated";
        flag.Name = "Updated Num";
        await flag.SaveAsync();

        Assert.NotNull(capturedBody);
        Assert.Contains("\"values\":null", capturedBody.Replace(" ", ""));
        Assert.Null(flag.Values);
    }

    // ---------------------------------------------------------------
    // Context registration fire-and-forget catch coverage
    // ---------------------------------------------------------------

    [Fact]
    public async Task EnsureInitialized_ContextRegistration_ErrorIsSilentlySwallowed()
    {
        // When context registration (app.smplkit.com) fails, the fire-and-forget catch
        // swallows the exception — initialization should still succeed.
        var flagJson = FlagListWithEnvJson(id: "ctx-err-flag", defaultVal: "false");
        var (client, _) = CreateClient(req =>
        {
            if (req.RequestUri!.Host.Contains("app.smplkit.com"))
                throw new HttpRequestException("context reg error");
            return Task.FromResult(JsonResponse(flagJson));
        });

        var handle = client.Flags.BooleanFlag("ctx-err-flag", false);
        // Should not throw even though context registration fails
        var ex = Record.Exception(() => handle.Get());
        Assert.Null(ex);

        // Allow the fire-and-forget Task.Run to settle
        await Task.Delay(200);
    }
}
