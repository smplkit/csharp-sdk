using System.Reflection;
using System.Text.Json;
using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for ConfigRuntime private/internal methods accessed via reflection.
/// These cover WebSocket message handling paths (HandleConfigChanged,
/// HandleConfigDeleted) and BuildWebSocketUrl that are otherwise unreachable
/// without a real WebSocket server.
/// </summary>
public class ConfigRuntimeInternalTests : IAsyncLifetime
{
    private ConfigRuntime? _runtime;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_runtime is not null)
        {
            try { await _runtime.DisposeAsync(); }
            catch { /* Ignore */ }
        }
    }

    private ConfigRuntime CreateRuntime(
        List<ConfigChainEntry>? chain = null,
        string environment = "production",
        Func<CancellationToken, Task<List<ConfigChainEntry>>>? fetchChainFn = null)
    {
        chain ??= new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 30, ["retries"] = 3 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 60 },
                },
            },
        };

        _runtime = new ConfigRuntime(
            configKey: "test_config",
            configId: "config-1",
            environment: environment,
            chain: chain,
            apiKey: "sk_test_key",
            fetchChainFn: fetchChainFn);
        return _runtime;
    }

    private static void InvokePrivate(object obj, string methodName, params object[] args)
    {
        var method = obj.GetType().GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(obj, args);
    }

    private static T InvokePrivate<T>(object obj, string methodName, params object[] args)
    {
        var method = obj.GetType().GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return (T)method!.Invoke(obj, args)!;
    }

    private static async Task InvokePrivateAsync(object obj, string methodName, params object[] args)
    {
        var method = obj.GetType().GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        await (Task)method!.Invoke(obj, args)!;
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — basic value update
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_UpdatesValue_UpdatesCacheAndFiresListeners()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();
        rt.OnChange(evt => events.Add(evt));

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 999}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal(999L, rt.Get("timeout"));

        // Listener should fire
        var timeoutEvent = events.Find(e => e.Key == "timeout");
        Assert.NotNull(timeoutEvent);
        Assert.Equal("websocket", timeoutEvent!.Source);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — value deletion (new_value is null, old_value is not)
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_DeletesValue_RemovesFromCache()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();
        rt.OnChange(evt => events.Add(evt));

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "retries", "old_value": 3}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // "retries" should be removed
        Assert.False(rt.Exists("retries"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — unknown config_id (not in chain)
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_UnknownConfigId_NoChange()
    {
        var rt = CreateRuntime();
        var originalTimeout = rt.Get("timeout");

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "unknown-id",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 999}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal(originalTimeout, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — missing config_id property
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_MissingConfigId_NoChange()
    {
        var rt = CreateRuntime();
        var originalTimeout = rt.Get("timeout");

        var changesJson = """
        {
            "type": "config_changed",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 999}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal(originalTimeout, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — missing changes property
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_MissingChanges_NoChange()
    {
        var rt = CreateRuntime();
        var originalTimeout = rt.Get("timeout");

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1"
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal(originalTimeout, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — env not in target's EnvValues
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_EnvNotInTarget_CreatesNewEnvEntry()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new(), // No envs
            },
        };

        var rt = CreateRuntime(chain, environment: "staging");

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "timeout", "old_value": 30, "new_value": 99}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // Cache should be re-resolved with updated values
        Assert.Equal(99L, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — multiple changes in single message
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_MultipleChanges_AllApplied()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();
        rt.OnChange(evt => events.Add(evt));

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 999},
                {"key": "retries", "old_value": 3, "new_value": 10},
                {"key": "new_key", "new_value": "hello"}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal(999L, rt.Get("timeout"));
        Assert.Equal(10L, rt.Get("retries"));
        Assert.Equal("hello", rt.Get("new_key"));

        // Should have events for all changed keys
        Assert.True(events.Count >= 2);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigDeleted — sets closed and disconnected
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigDeleted_SetsClosedAndDisconnected()
    {
        var rt = CreateRuntime();
        InvokePrivate(rt, "HandleConfigDeleted");

        Assert.Equal("disconnected", rt.ConnectionStatus());

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // BuildWebSocketUrl — https:// prefix
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildWebSocketUrl_HttpsPrefix_ReturnsWssUrl()
    {
        var rt = CreateRuntime();
        var url = InvokePrivate<string>(rt, "BuildWebSocketUrl");

        Assert.StartsWith("wss://", url);
        Assert.Contains("/api/ws/v1/configs", url);
        Assert.Contains("api_key=", url);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // BuildWebSocketUrl — api key is URL-encoded
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildWebSocketUrl_ApiKeyIsUrlEncoded()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new(),
                EnvValues = new(),
            },
        };

        // Create runtime with an API key that has special characters
        _runtime = new ConfigRuntime(
            configKey: "test",
            configId: "c1",
            environment: "prod",
            chain: chain,
            apiKey: "sk_test key&value=special",
            fetchChainFn: null);

        var url = InvokePrivate<string>(_runtime, "BuildWebSocketUrl");

        Assert.Contains("api_key=sk_test", url);
        // The special characters should be URL-encoded
        Assert.DoesNotContain(" ", url.Split("api_key=")[1]);

        await _runtime.CloseAsync();
    }

    // ------------------------------------------------------------------
    // FireChangeListeners — listener exception is swallowed
    // ------------------------------------------------------------------

    [Fact]
    public async Task FireChangeListeners_ExceptionInListener_DoesNotStopOthers()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();

        // First listener throws
        rt.OnChange(_ => throw new InvalidOperationException("boom"));
        // Second listener should still fire
        rt.OnChange(evt => events.Add(evt));

        // Trigger change via HandleConfigChanged
        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 999}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // Second listener should have fired despite first throwing
        Assert.NotEmpty(events);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // FireChangeListeners — no change = no events
    // ------------------------------------------------------------------

    [Fact]
    public async Task FireChangeListeners_NoChange_NoEvents()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();
        rt.OnChange(evt => events.Add(evt));

        // Apply a "change" where new_value equals old_value for base values
        // (the resolve will produce same result)
        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 60}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // The runtime fires a change event because the JSON-deserialized value (long 60)
        // differs in type from the initial cache value (int 60), even though numerically equal.
        Assert.Single(events);
        Assert.Equal("timeout", events[0].Key);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — with new_value as complex object
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_ComplexNewValue_NormalizesAndApplies()
    {
        var rt = CreateRuntime();

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "nested", "new_value": {"a": 1, "b": "two"}}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        var nested = rt.Get("nested");
        Assert.NotNull(nested);
        var dict = Assert.IsType<Dictionary<string, object?>>(nested);
        Assert.Equal(1L, dict["a"]);
        Assert.Equal("two", dict["b"]);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync — tested directly via reflection
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncCacheAsync_UpdatesCacheAndFiresListeners()
    {
        var events = new List<ConfigChangeEvent>();
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999, ["retries"] = 3 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 999 },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt));

        await InvokePrivateAsync(rt, "ResyncCacheAsync", CancellationToken.None);

        Assert.Equal(999, rt.Get("timeout"));
        var timeoutEvent = events.Find(e => e.Key == "timeout");
        Assert.NotNull(timeoutEvent);
        Assert.Equal("websocket", timeoutEvent!.Source);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync — null fetchChainFn returns early
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncCacheAsync_NullFetchFn_DoesNothing()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["key"] = "value" },
                EnvValues = new(),
            },
        };

        _runtime = new ConfigRuntime(
            configKey: "test",
            configId: "c1",
            environment: "prod",
            chain: chain,
            apiKey: "sk_test",
            fetchChainFn: null);

        await InvokePrivateAsync(_runtime, "ResyncCacheAsync", CancellationToken.None);

        Assert.Equal("value", _runtime.Get("key"));
        await _runtime.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync — fetch throws, exception swallowed
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncCacheAsync_FetchThrows_SwallowsException()
    {
        var rt = CreateRuntime(fetchChainFn: _ =>
            throw new Exception("fetch boom"));

        // Should not throw
        await InvokePrivateAsync(rt, "ResyncCacheAsync", CancellationToken.None);

        // Old cache intact
        Assert.Equal(60, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync — updates stats
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncCacheAsync_UpdatesFetchCountAndLastFetchAt()
    {
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 30, ["retries"] = 3 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 60 },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        var beforeStats = rt.Stats();

        await InvokePrivateAsync(rt, "ResyncCacheAsync", CancellationToken.None);

        var afterStats = rt.Stats();
        Assert.True(afterStats.FetchCount > beforeStats.FetchCount);
        Assert.NotNull(afterStats.LastFetchAt);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — change with both old and new value null
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_BothOldAndNewNull_SetsNullValue()
    {
        var rt = CreateRuntime();

        // When new_value is explicitly null (present but null) and old_value is also null,
        // This is the else branch (newVal is null but oldVal is also null), so it goes
        // to the else branch and sets the key to null
        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "new_nullable", "new_value": null}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // The key should exist but be null (because Normalize(null JsonElement) = null,
        // but since there is no old_value, oldVal is null, and newVal is null,
        // the condition newVal is null && oldVal is not null is false,
        // so it goes to the else branch: newBaseValues[key] = null
        Assert.True(rt.Exists("new_nullable"));
        Assert.Null(rt.Get("new_nullable"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — change where only old_value present (deletion)
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_OnlyOldValue_RemovesKey()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["to_delete"] = "exists", ["keep"] = "kept" },
                EnvValues = new()
                {
                    ["production"] = new() { ["to_delete"] = "env_val" },
                },
            },
        };

        var rt = CreateRuntime(chain);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "to_delete", "old_value": "exists"}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // "to_delete" should be removed from both base and env values
        Assert.False(rt.Exists("to_delete"));
        Assert.Equal("kept", rt.Get("keep"));

        await rt.CloseAsync();
    }
}
