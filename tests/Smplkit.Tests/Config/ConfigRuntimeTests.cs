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
/// resolved-value Get / GetValue / GetValueOr, OnChange listeners,
/// RefreshAsync, and the event stream handlers (HandleConfigChanged /
/// HandleConfigDeleted / HandleConfigsChanged).
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
                            "test": {"host": "test-host"}
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

    // Transitivity fixture: leaf → mid → root. The initial list returns ONLY
    // the leaf; mid and root are uncached (e.g. parents created via discovery
    // after connect that never broadcast their own event).
    private const string AncestorLeafOnlyListJson = """
        {
            "data": [
                {
                    "id": "leaf",
                    "type": "config",
                    "attributes": {
                        "id": "leaf", "name": "Leaf", "description": null, "parent": "mid",
                        "items": {"leaf_key": {"value": "leaf-val", "type": "STRING"}},
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z", "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            ]
        }
        """;

    private const string AncestorLeafJson = """
        {
            "data": {
                "id": "leaf", "type": "config",
                "attributes": {
                    "id": "leaf", "name": "Leaf", "description": null, "parent": "mid",
                    "items": {"leaf_key": {"value": "leaf-val", "type": "STRING"}},
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z", "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string AncestorMidJson = """
        {
            "data": {
                "id": "mid", "type": "config",
                "attributes": {
                    "id": "mid", "name": "Mid", "description": null, "parent": "root",
                    "items": {"mid_key": {"value": "mid-val", "type": "STRING"}},
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z", "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    private const string AncestorRootJson = """
        {
            "data": {
                "id": "root", "type": "config",
                "attributes": {
                    "id": "root", "name": "Root", "description": null, "parent": null,
                    "items": {"root_key": {"value": "root-val", "type": "STRING"}},
                    "environments": {},
                    "created_at": "2024-01-15T10:30:00Z", "updated_at": "2024-01-15T10:30:00Z"
                }
            }
        }
        """;

    [Fact]
    public void HandleConfigChanged_FetchesUncachedAncestors_InheritedValuesSurvive()
    {
        var (client, _) = MakeClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.Contains("config."))
            {
                if (url.Contains("/configs/leaf")) return Task.FromResult(Json(AncestorLeafJson));
                if (url.Contains("/configs/mid")) return Task.FromResult(Json(AncestorMidJson));
                if (url.Contains("/configs/root")) return Task.FromResult(Json(AncestorRootJson));
                // List endpoint (no trailing id): only "leaf" is present.
                return Task.FromResult(Json(AncestorLeafOnlyListJson));
            }
            return Task.FromResult(Json("{}"));
        });

        // Initial connect sees only "leaf"; its parent "mid" and grandparent
        // "root" aren't cached, so inherited values aren't resolved yet.
        var before = client.Config.Subscribe("leaf");
        Assert.Equal("leaf-val", before["leaf_key"]);
        Assert.Null(before.GetOrDefault("mid_key", null));
        Assert.Null(before.GetOrDefault("root_key", null));

        // A config_changed for the leaf must transitively pull in the uncached
        // parent (mid) AND grandparent (root) so the full chain re-resolves.
        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "leaf" },
        });

        var after = client.Config.Subscribe("leaf");
        Assert.Equal("leaf-val", after["leaf_key"]);
        Assert.Equal("mid-val", after.GetOrDefault("mid_key", null));   // parent fetched
        Assert.Equal("root-val", after.GetOrDefault("root_key", null)); // grandparent fetched transitively

        // A second config_changed finds mid + root already cached, so the
        // ancestor walk skips re-fetching them and the chain still resolves.
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "leaf" },
        });
        var again = client.Config.Subscribe("leaf");
        Assert.Equal("mid-val", again.GetOrDefault("mid_key", null));
        Assert.Equal("root-val", again.GetOrDefault("root_key", null));
    }

    [Fact]
    public void HandleConfigChanged_AncestorFetchReturnsNull_Skipped()
    {
        const string orphanOnlyList = """
            {
                "data": [
                    {
                        "id": "orphan", "type": "config",
                        "attributes": {
                            "id": "orphan", "name": "Orphan", "description": null, "parent": "ghost",
                            "items": {"orphan_key": {"value": "orphan-val", "type": "STRING"}},
                            "environments": {},
                            "created_at": "2024-01-15T10:30:00Z", "updated_at": "2024-01-15T10:30:00Z"
                        }
                    }
                ]
            }
            """;
        const string orphanSingle = """
            {
                "data": {
                    "id": "orphan", "type": "config",
                    "attributes": {
                        "id": "orphan", "name": "Orphan", "description": null, "parent": "ghost",
                        "items": {"orphan_key": {"value": "orphan-val", "type": "STRING"}},
                        "environments": {},
                        "created_at": "2024-01-15T10:30:00Z", "updated_at": "2024-01-15T10:30:00Z"
                    }
                }
            }
            """;
        var (client, _) = MakeClient(req =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.Contains("config."))
            {
                // The uncached parent "ghost" resolves to no data — the walk skips it.
                if (url.Contains("/configs/ghost")) return Task.FromResult(Json("""{"data":null}"""));
                if (url.Contains("/configs/orphan")) return Task.FromResult(Json(orphanSingle));
                return Task.FromResult(Json(orphanOnlyList));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Config.Subscribe("orphan");

        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Should not throw even though the ancestor fetch yields no config.
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "orphan" },
        });

        var values = client.Config.Subscribe("orphan");
        Assert.Equal("orphan-val", values["orphan_key"]);
    }

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

        var values = client.Config.Subscribe("user-svc");
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
        Assert.Throws<NotFoundException>(() => client.Config.Subscribe("does-not-exist"));
    }

    [Fact]
    public void Get_ReturnsLiveProxy_ReadOnly()
    {
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
                return Task.FromResult(Json(ConfigListJson));
            return Task.FromResult(Json("{}"));
        });

        // LiveConfigProxy implements IReadOnlyDictionary — no setter exposed,
        // so customer mutation is impossible at the surface.
        var proxy = client.Config.Subscribe("user-svc");
        Assert.IsType<Smplkit.Config.LiveConfigProxy>(proxy);
        Assert.Equal("user-svc", proxy.ConfigId);
        // Verify the proxy stays consistent across calls (identity-stable values).
        Assert.Equal(proxy["host"], client.Config.Subscribe("user-svc")["host"]);
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
        client.Config.Subscribe("user-svc");
        client.Config.Subscribe("user-svc");
        client.Config.Subscribe("common");
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

        client.Config.Subscribe("user-svc"); // triggers init (1st list)
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
        method.Invoke(client.Config, new object?[] { oldCache, newCache, "push" });

        Assert.Single(events);
        Assert.Null(events[0].OldValue);
        Assert.Equal("v", events[0].NewValue);
        Assert.Equal("push", events[0].Source);
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

    // Event stream handlers — invoked via reflection to verify behavior

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

        client.Config.Subscribe("user-svc"); // initializes via list

        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "user-svc" },
        });

        // Second Get returns the refreshed value
        var values = client.Config.Subscribe("user-svc");
        Assert.Equal("new-host", values["host"]);
    }

    [Fact]
    public void HandleConfigChanged_NoIdInData_NoOp()
    {
        var (client, _) = MakeClient(_ => Task.FromResult(Json(ConfigListJson)));
        client.Config.Subscribe("user-svc"); // initialize

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
        client.Config.Subscribe("user-svc");

        var handler = typeof(ConfigClient).GetMethod("HandleConfigDeleted",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "user-svc" },
        });

        // After deletion, the config is gone from cache
        Assert.Throws<NotFoundException>(() => client.Config.Subscribe("user-svc"));
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

        client.Config.Subscribe("user-svc"); // initialize

        var handler = typeof(ConfigClient).GetMethod("HandleConfigsChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?>(),
        });

        // Cache should still work
        var values = client.Config.Subscribe("user-svc");
        Assert.NotNull(values);
    }

    [Fact]
    public void HandleReconnectRefetch_ReusesConfigsChangedBulkPath()
    {
        int listCall = 0;
        var (client, _) = MakeClient(req =>
        {
            if (req.RequestUri!.ToString().Contains("config."))
            {
                listCall++;
                var body = listCall <= 1
                    ? ConfigListJson
                    : ConfigListJson.Replace(
                        "\"retries\": {\"value\": 3, \"type\": \"NUMBER\"}",
                        "\"retries\": {\"value\": 7, \"type\": \"NUMBER\"}");
                return Task.FromResult(Json(body));
            }
            return Task.FromResult(Json("{}"));
        });

        client.Config.Subscribe("user-svc"); // initialize (list call 1)

        var events = new List<ConfigChangeEvent>();
        client.Config.OnChange(evt => events.Add(evt));

        var handler = typeof(ConfigClient).GetMethod("HandleReconnectRefetch",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        handler.Invoke(client.Config, Array.Empty<object>());

        // The refetch reuses the configs_changed bulk path: only the moved
        // value fires, with the push source label, and the cache is refreshed.
        Assert.Contains(events, e => e.ItemKey == "retries" && e.Source == "push");
        Assert.Equal(7L, client.Config.GetValue("user-svc", "retries"));
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

        client.Config.Subscribe("user-svc"); // succeeds (call 1)

        var handler = typeof(ConfigClient).GetMethod("HandleConfigChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Should not throw even if refetch fails
        handler.Invoke(client.Config, new object?[]
        {
            new Dictionary<string, object?> { ["id"] = "user-svc" },
        });
    }
}
