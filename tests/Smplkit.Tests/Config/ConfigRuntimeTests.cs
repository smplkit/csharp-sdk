using System.Net;
using System.Text;
using Smplkit;
using Smplkit.Config;
using Smplkit.Errors;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for the runtime <see cref="ConfigClient"/>: lazy initialization,
/// resolved-value Get / Get&lt;T&gt;, OnChange listeners, RefreshAsync, and the
/// WebSocket event handlers (HandleConfigChanged / HandleConfigDeleted /
/// HandleConfigsChanged).
/// </summary>
public class ConfigRuntimeTests
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

    private const string ConfigListJson = """
        {
            "data": [
                {
                    "id": "user-svc",
                    "type": "config",
                    "attributes": {
                        "id": "user-svc",
                        "name": "User Service",
                        "description": null,
                        "parent": "common",
                        "items": {
                            "host": {"value": "default-host", "type": "STRING"},
                            "retries": {"value": 3, "type": "NUMBER"},
                            "database.port": {"value": 5432, "type": "NUMBER"}
                        },
                        "environments": {
                            "test": {"values": {"host": {"value": "test-host", "type": "STRING"}}}
                        },
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                },
                {
                    "id": "common",
                    "type": "config",
                    "attributes": {
                        "id": "common",
                        "name": "Common",
                        "description": null,
                        "parent": null,
                        "items": {
                            "shared": {"value": "common-val", "type": "STRING"}
                        },
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z",
                        "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private const string SingleConfigJson = """
        {
            "data": {
                "id": "user-svc",
                "type": "config",
                "attributes": {
                    "id": "user-svc",
                    "name": "User Service",
                    "description": "updated",
                    "parent": null,
                    "items": {
                        "host": {"value": "new-host", "type": "STRING"}
                    },
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z",
                    "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    [Fact]
    public void Get_ResolvesValuesFromCache()
    {
        var (client, _) = MakeClient(req =>
        {
            // App-level requests pass through without affecting the cache
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });

        var values = client.Config.Get("user-svc");
        Assert.Equal("test-host", values["host"]); // env override wins
        Assert.Equal(3L, values["retries"]);       // base value
        Assert.Equal("common-val", values["shared"]); // inherited from parent
    }

    [Fact]
    public void Get_UnknownConfig_ThrowsNotFound()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });
        Assert.Throws<NotFoundException>(() => client.Config.Get("does-not-exist"));
    }

    [Fact]
    public void Get_ReturnsDefensiveCopy()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });

        var v1 = client.Config.Get("user-svc");
        v1["host"] = "tampered";
        var v2 = client.Config.Get("user-svc");
        Assert.NotEqual("tampered", v2["host"]);
    }

    public sealed class UserSvcModel
    {
        public string? Host { get; set; }
        public int Retries { get; set; }
    }

    [Fact]
    public void GetTyped_DeserializesToTargetType()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });
        var model = client.Config.Get<UserSvcModel>("user-svc");
        Assert.Equal("test-host", model.Host);
        Assert.Equal(3, model.Retries);
    }

    public sealed class NestedDb { public string? Host { get; set; } public int Port { get; set; } }
    public sealed class NestedModel { public NestedDb Database { get; set; } = new(); }

    [Fact]
    public void GetTyped_ExpandsDotNotationKeys()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });
        var model = client.Config.Get<NestedModel>("user-svc");
        Assert.Equal(5432, model.Database.Port);
    }

    [Fact]
    public void Get_LazyInit_Idempotent()
    {
        int listCalls = 0;
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config.") && req.Method == HttpMethod.Get)
            {
                listCalls++;
                return Task.FromResult(Json(ConfigListJson));
            }
            return Task.FromResult(Json("{}"));
        });

        // Multiple Get calls only trigger one list
        client.Config.Get("user-svc");
        client.Config.Get("user-svc");
        client.Config.Get("common");
        Assert.Equal(1, listCalls);
    }

    [Fact]
    public async Task RefreshAsync_FetchesAndDiffs()
    {
        int listCalls = 0;
        var responses = new[] { ConfigListJson, ConfigListJson };
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config.") && req.Method == HttpMethod.Get)
            {
                var response = responses[Math.Min(listCalls, responses.Length - 1)];
                listCalls++;
                return Task.FromResult(Json(response));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Config.Get("user-svc"); // triggers init (1st list)
        await client.Config.RefreshAsync();
        Assert.True(listCalls >= 2);
    }

    [Fact]
    public void OnChange_GlobalListener_Registers()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });

        bool fired = false;
        client.Config.OnChange(_ => fired = true);

        // Use internal DiffAndFire via reflection to simulate a change
        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "old" }
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "new" }
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "test" });

        Assert.True(fired);
    }

    [Fact]
    public void OnChange_ConfigScopedListener_FiresOnlyForMatchingId()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));

        var fired = new List<ConfigChangeEvent>();
        client.Config.OnChange("c1", evt => fired.Add(evt));

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "old" },
            ["c2"] = new() { ["k"] = "old" },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "new" },
            ["c2"] = new() { ["k"] = "new" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "test" });

        Assert.Single(fired);
        Assert.Equal("c1", fired[0].ConfigId);
    }

    [Fact]
    public void OnChange_ItemScopedListener_FiresOnlyForMatchingItem()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));

        var fired = new List<ConfigChangeEvent>();
        client.Config.OnChange("c1", "watched", evt => fired.Add(evt));

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["watched"] = "old", ["other"] = "old" },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["watched"] = "new", ["other"] = "new" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "src" });

        Assert.Single(fired);
        Assert.Equal("watched", fired[0].ItemKey);
    }

    [Fact]
    public void OnChange_ListenerThrows_DoesNotPropagate()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));

        client.Config.OnChange(_ => throw new InvalidOperationException("listener bug"));
        bool secondFired = false;
        client.Config.OnChange(_ => secondFired = true);

        var oldCache = new Dictionary<string, Dictionary<string, object?>>();
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "v" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "src" });

        Assert.True(secondFired);
    }

    [Fact]
    public void DiffAndFire_NoChanges_FiresNothing()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var fired = false;
        client.Config.OnChange(_ => fired = true);

        var same = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "v" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { same, same, "src" });

        Assert.False(fired);
    }

    [Fact]
    public void DiffAndFire_AddedKey_Fires()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        var oldCache = new Dictionary<string, Dictionary<string, object?>>();
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["new-key"] = "v" },
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "websocket" });

        Assert.Single(events);
        Assert.Null(events[0].OldValue);
        Assert.Equal("v", events[0].NewValue);
        Assert.Equal("websocket", events[0].Source);
    }

    [Fact]
    public void DiffAndFire_RemovedKey_Fires()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        var oldCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new() { ["k"] = "old" },
        };
        var newCache = new Dictionary<string, Dictionary<string, object?>>
        {
            ["c1"] = new Dictionary<string, object?>(),
        };
        var method = typeof(ConfigClient).GetMethod("DiffAndFire",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "src" });

        Assert.Single(events);
        Assert.Equal("old", events[0].OldValue);
        Assert.Null(events[0].NewValue);
    }

    // WebSocket event handlers — invoked via reflection to verify behavior

    [Fact]
    public void HandleConfigChanged_RefreshesCache()
    {
        var requestNum = 0;
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config.") && req.Method == HttpMethod.Get)
            {
                requestNum++;
                // 1st: initial list, 2nd+: GET single config (after handler invocation)
                if (req.RequestUri.ToString().Contains("/configs/"))
                    return Task.FromResult(Json(SingleConfigJson));
                return Task.FromResult(Json(ConfigListJson));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Config.Get("user-svc"); // initializes via list

        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "user-svc" },
        });

        // Second Get returns the refreshed value
        var values = client.Config.Get("user-svc");
        Assert.Equal("new-host", values["host"]);
    }

    [Fact]
    public void HandleConfigChanged_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        client.Config.Get("user-svc"); // initialize

        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Invoking with no id should not throw
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?>(),
        });
    }

    [Fact]
    public void HandleConfigDeleted_RemovesFromCache()
    {
        int requestNumber = 0;
        var (client, _) = MakeClient(req =>
        {
            requestNumber++;
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });

        // Initialize
        client.Config.Get("user-svc");

        var handler = typeof(ConfigClient).GetMethod("HandleConfigDeleted",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "user-svc" },
        });

        // After deletion, the config is gone from cache
        Assert.Throws<NotFoundException>(() => client.Config.Get("user-svc"));
    }

    [Fact]
    public void HandleConfigsChanged_RefetchesAll()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });

        client.Config.Get("user-svc"); // initialize

        var handler = typeof(ConfigClient).GetMethod("HandleConfigsChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?>(),
        });

        // Cache should still work
        var values = client.Config.Get("user-svc");
        Assert.NotNull(values);
    }

    [Fact]
    public void HandleConfigChanged_ServerError_Swallowed()
    {
        int call = 0;
        var (client, _) = MakeClient(req =>
        {
            call++;
            if (req.RequestUri!.ToString().Contains("config."))
            {
                if (call <= 1) return Task.FromResult(Json(ConfigListJson));
                return Task.FromResult(Json("""{"errors":[{"detail":"x"}]}""", HttpStatusCode.InternalServerError));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Config.Get("user-svc"); // succeeds (call 1)

        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Should not throw even if refetch fails
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "user-svc" },
        });
    }
}
