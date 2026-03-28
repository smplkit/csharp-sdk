using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Additional tests targeting uncovered code paths in ConfigRuntime for 100% coverage.
/// Uses reflection to reach private/internal methods and fields that are otherwise
/// unreachable without a real WebSocket server.
/// </summary>
public class ConfigRuntimeCoverageTests : IAsyncLifetime
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
        string apiKey = "sk_test_key",
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
            apiKey: apiKey,
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

    private static T? GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T?)field!.GetValue(obj);
    }

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(obj, value);
    }

    // ------------------------------------------------------------------
    // ReceiveRawAsync — ws is null
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReceiveRawAsync_WsIsNull_ReturnsNull()
    {
        var rt = CreateRuntime();

        // Set _ws to null via reflection
        SetPrivateField(rt, "_ws", null);

        var method = rt.GetType().GetMethod("ReceiveRawAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<string?>)method!.Invoke(rt, new object[] { CancellationToken.None })!;
        var result = await task;

        Assert.Null(result);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ReceiveRawAsync — ws not in Open state
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReceiveRawAsync_WsNotOpen_ReturnsNull()
    {
        var rt = CreateRuntime();

        // Create a new ClientWebSocket (state is None, not Open)
        var ws = new ClientWebSocket();
        SetPrivateField(rt, "_ws", ws);

        var method = rt.GetType().GetMethod("ReceiveRawAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var task = (Task<string?>)method!.Invoke(rt, new object[] { CancellationToken.None })!;
        var result = await task;

        Assert.Null(result);
        ws.Dispose();
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ReceiveLoopAsync — null raw breaks out of loop
    // ------------------------------------------------------------------

    [Fact]
    public async Task ReceiveLoopAsync_NullRaw_BreaksLoop()
    {
        var rt = CreateRuntime();

        // Set _ws to null so ReceiveRawAsync returns null immediately
        SetPrivateField(rt, "_ws", null);

        // ReceiveLoopAsync should exit gracefully when ReceiveRawAsync returns null
        await InvokePrivateAsync(rt, "ReceiveLoopAsync", CancellationToken.None);

        // If we get here without hanging, the loop exited
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ReceiveLoopAsync — message without "type" property is skipped
    // ------------------------------------------------------------------

    // This is tested indirectly: ReceiveLoopAsync calls ReceiveRawAsync which
    // returns null when ws is not open, causing the loop to break.
    // The `continue` for missing type is inside the loop which we can't reach
    // without a real WebSocket sending messages.

    // ------------------------------------------------------------------
    // HandleConfigChanged — change where new_value is present but null
    // and old_value is present and non-null => deletion
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_NewValueExplicitlyNull_OldValuePresent_DeletesKey()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["to_remove"] = "val", ["keep"] = "kept" },
                EnvValues = new()
                {
                    ["production"] = new() { ["to_remove"] = "env_val" },
                },
            },
        };

        var rt = CreateRuntime(chain);

        // new_value is explicitly null in JSON, old_value is present
        // Normalize(null JsonElement) returns null, so newVal is null
        // Normalize("val") returns "val", so oldVal is not null
        // This triggers the deletion branch
        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "to_remove", "old_value": "val", "new_value": null}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.False(rt.Exists("to_remove"));
        Assert.Equal("kept", rt.Get("keep"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — change with array new_value
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_ArrayNewValue_NormalizesCorrectly()
    {
        var rt = CreateRuntime();

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "tags", "new_value": ["a", "b", "c"]}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        var tags = rt.Get("tags");
        Assert.NotNull(tags);
        var arr = Assert.IsType<object?[]>(tags);
        Assert.Equal(3, arr.Length);
        Assert.Equal("a", arr[0]);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — change with boolean new_value
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_BoolNewValue_NormalizesCorrectly()
    {
        var rt = CreateRuntime();

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "enabled", "new_value": false}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal(false, rt.Get("enabled"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — updates env values for matching environment
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_UpdatesExistingEnvValues()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 60, ["retries"] = 3 },
                },
            },
        };

        var rt = CreateRuntime(chain);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "timeout", "old_value": 60, "new_value": 120}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // Should resolve with updated env value
        Assert.Equal(120L, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // CloseAsync — when _ws is null (never connected)
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WsIsNull_CompletesGracefully()
    {
        var rt = CreateRuntime();

        // Set _ws to null
        SetPrivateField(rt, "_ws", null);

        await rt.CloseAsync();
        // When _ws is null (never connected), CloseAsync completes without error
        // but status remains unchanged since no WS was active
        var status = rt.ConnectionStatus();
        Assert.True(status == "disconnected" || status == "connecting",
            $"Expected disconnected or connecting, got: {status}");
    }

    // ------------------------------------------------------------------
    // CloseAsync — when _ws exists but is not in Open state
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WsNotOpen_SkipsCloseAndDisposes()
    {
        var rt = CreateRuntime();

        // Create a WebSocket in None state (not connected)
        var ws = new ClientWebSocket();
        SetPrivateField(rt, "_ws", ws);

        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync — with fetchChainFn that returns different sized chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncCacheAsync_DifferentChainSize_UpdatesFetchCountCorrectly()
    {
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999 },
                EnvValues = new(),
            },
            new()
            {
                Id = "config-2",
                Values = new() { ["extra"] = "val" },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));

        var beforeCount = rt.Stats().FetchCount; // 1 (initial chain has 1 entry)
        await InvokePrivateAsync(rt, "ResyncCacheAsync", CancellationToken.None);

        var afterCount = rt.Stats().FetchCount;
        // Should add 2 (new chain has 2 entries)
        Assert.Equal(beforeCount + 2, afterCount);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — change that adds a brand new key
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_AddsNewKey_FiresListenerForAddedKey()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();
        rt.OnChange(evt => events.Add(evt));

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "brand_new", "new_value": "hello"}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal("hello", rt.Get("brand_new"));

        var addedEvent = events.Find(e => e.Key == "brand_new");
        Assert.NotNull(addedEvent);
        Assert.Null(addedEvent!.OldValue);
        Assert.Equal("hello", addedEvent.NewValue);
        Assert.Equal("websocket", addedEvent.Source);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigDeleted — verify it stops the runtime
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigDeleted_SetsClosed_PreventsFurtherWsLoop()
    {
        var rt = CreateRuntime();

        InvokePrivate(rt, "HandleConfigDeleted");

        // _closed should be true and connectionStatus disconnected
        Assert.Equal("disconnected", rt.ConnectionStatus());

        // Values should still be accessible
        Assert.Equal(60, rt.Get("timeout"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // RunWebSocketAsync — backoff with increasing attempt numbers
    // The background task exercises this, but we can verify the
    // BackoffSeconds array is used correctly via the static field.
    // ------------------------------------------------------------------

    [Fact]
    public async Task BackoffSeconds_HasCorrectValues()
    {
        var field = typeof(ConfigRuntime).GetField("BackoffSeconds",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var backoff = (int[])field!.GetValue(null)!;
        Assert.Equal(new[] { 1, 2, 4, 8, 16, 32, 60 }, backoff);

        // Just to keep the dispose happy
        var rt = CreateRuntime();
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // RunWebSocketAsync — OperationCanceledException when closed breaks loop
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_DuringBackoff_BreaksLoopViaCancel()
    {
        // This tests the Task.Delay cancellation path in RunWebSocketAsync
        var rt = CreateRuntime();

        // Give the background task time to enter the backoff delay
        await Task.Delay(200);

        // Close should cancel the CTS, causing Task.Delay to throw OperationCanceledException
        await rt.CloseAsync();

        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // FireChangeListeners — key removed (in oldCache but not newCache)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FireChangeListeners_KeyRemoved_FiresWithNullNewValue()
    {
        var events = new List<ConfigChangeEvent>();
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["a"] = 1, ["b"] = 2 },
                EnvValues = new(),
            },
        };

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["a"] = 1 },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, environment: "none", fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt));

        await rt.RefreshAsync();

        var removedEvent = events.Find(e => e.Key == "b");
        Assert.NotNull(removedEvent);
        Assert.Equal(2, removedEvent!.OldValue);
        Assert.Null(removedEvent.NewValue);
        Assert.Equal("manual", removedEvent.Source);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // FireChangeListeners — key-filtered listener does NOT fire for
    // non-matching key (additional verification)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FireChangeListeners_KeyFiltered_DoesNotFireForOtherKeys()
    {
        var events = new List<ConfigChangeEvent>();
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["a"] = 1, ["b"] = 2 },
                EnvValues = new(),
            },
        };

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["a"] = 99, ["b"] = 99 },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, environment: "none", fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt), key: "b");

        await rt.RefreshAsync();

        // Should only fire for "b", not "a"
        Assert.Single(events);
        Assert.Equal("b", events[0].Key);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — multiple deletions in single message
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_MultipleDeletions_AllRemoved()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["a"] = 1, ["b"] = 2, ["c"] = 3 },
                EnvValues = new()
                {
                    ["production"] = new() { ["a"] = 10 },
                },
            },
        };

        var rt = CreateRuntime(chain);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "a", "old_value": 10},
                {"key": "b", "old_value": 2}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.False(rt.Exists("a"));
        Assert.False(rt.Exists("b"));
        Assert.True(rt.Exists("c"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — update and delete in same message
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_MixedUpdateAndDelete_BothApplied()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["keep"] = "old", ["remove_me"] = "bye" },
                EnvValues = new()
                {
                    ["production"] = new() { ["keep"] = "env_old" },
                },
            },
        };

        var rt = CreateRuntime(chain);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "keep", "old_value": "env_old", "new_value": "updated"},
                {"key": "remove_me", "old_value": "bye"}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Equal("updated", rt.Get("keep"));
        Assert.False(rt.Exists("remove_me"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // BuildWebSocketUrl — verify full URL format
    // ------------------------------------------------------------------

    [Fact]
    public async Task BuildWebSocketUrl_ProducesCorrectUrl()
    {
        var rt = CreateRuntime(apiKey: "sk_test_123");

        var url = InvokePrivate<string>(rt, "BuildWebSocketUrl");

        // Should be wss:// (since base is https://)
        Assert.StartsWith("wss://config.smplkit.com", url);
        Assert.Contains("/api/ws/v1/configs", url);
        Assert.Contains("api_key=sk_test_123", url);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // DisposeAsync wraps CloseAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_CallsCloseAsync()
    {
        var rt = CreateRuntime();

        // DisposeAsync should call CloseAsync internally
        await rt.DisposeAsync();

        Assert.Equal("disconnected", rt.ConnectionStatus());
        // Calling dispose again should be safe
        await rt.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // CloseAsync — exercises WaitAsync timeout on _wsTask
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WaitsForWsTask_WithTimeout()
    {
        // Create runtime with a slow fetchChainFn so the background task is busy
        var rt = CreateRuntime(fetchChainFn: async ct =>
        {
            await Task.Delay(5000, ct);
            return new List<ConfigChainEntry>();
        });

        // Give background task time to start
        await Task.Delay(100);

        // CloseAsync should complete within reasonable time even if wsTask is slow
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await rt.CloseAsync(timeoutCts.Token);

        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // GetInt with double value NaN returns default
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_DoubleNaN_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["nan"] = double.NaN },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("nan"));
        Assert.Equal(42, rt.GetInt("nan", 42));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetInt with double PositiveInfinity returns default
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_DoubleInfinity_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["inf"] = double.PositiveInfinity },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("inf"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — string old_value with no new_value triggers deletion
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_StringOldValue_NoNewValue_DeletesFromBothBaseAndEnv()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["name"] = "test" },
                EnvValues = new()
                {
                    ["production"] = new() { ["name"] = "prod-test" },
                },
            },
        };

        var rt = CreateRuntime(chain);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "name", "old_value": "prod-test"}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // After deletion, "name" should not exist (both base and env removed)
        Assert.False(rt.Exists("name"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — change on multi-entry chain targets correct entry
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_MultiEntryChain_TargetsCorrectEntry()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["child_key"] = "child_val" },
                EnvValues = new(),
            },
            new()
            {
                Id = "parent",
                Values = new() { ["parent_key"] = "parent_val" },
                EnvValues = new(),
            },
        };

        _runtime = new ConfigRuntime(
            configKey: "test",
            configId: "child",
            environment: "production",
            chain: chain,
            apiKey: "sk_test",
            fetchChainFn: null);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "parent",
            "changes": [
                {"key": "parent_key", "old_value": "parent_val", "new_value": "updated_parent"}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(_runtime, "HandleConfigChanged", doc.RootElement);

        // Parent key should be updated
        Assert.Equal("updated_parent", _runtime.Get("parent_key"));
        // Child key should be unchanged
        Assert.Equal("child_val", _runtime.Get("child_key"));

        await _runtime.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — numeric old_value triggers deletion
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_NumericOldValue_NoNewValue_DeletesKey()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["count"] = 5 },
                EnvValues = new()
                {
                    ["production"] = new() { ["count"] = 10 },
                },
            },
        };

        var rt = CreateRuntime(chain);

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": [
                {"key": "count", "old_value": 10}
            ]
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        // After deletion, "count" should not exist
        Assert.False(rt.Exists("count"));

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // WsBasePath constant verification
    // ------------------------------------------------------------------

    [Fact]
    public async Task WsBasePath_IsCorrect()
    {
        var rt = CreateRuntime();
        var url = InvokePrivate<string>(rt, "BuildWebSocketUrl");
        Assert.Contains("/api/ws/v1/configs", url);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync — fires listeners for key additions and removals
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResyncCacheAsync_WithKeyChanges_FiresCorrectEvents()
    {
        var events = new List<ConfigChangeEvent>();
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["old_key"] = "old_val" },
                EnvValues = new(),
            },
        };

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["new_key"] = "new_val" },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, environment: "none", fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt));

        await InvokePrivateAsync(rt, "ResyncCacheAsync", CancellationToken.None);

        // Should have events for removal of old_key and addition of new_key
        var removedEvent = events.Find(e => e.Key == "old_key");
        Assert.NotNull(removedEvent);
        Assert.Equal("old_val", removedEvent!.OldValue);
        Assert.Null(removedEvent.NewValue);
        Assert.Equal("websocket", removedEvent.Source);

        var addedEvent = events.Find(e => e.Key == "new_key");
        Assert.NotNull(addedEvent);
        Assert.Null(addedEvent!.OldValue);
        Assert.Equal("new_val", addedEvent.NewValue);
        Assert.Equal("websocket", addedEvent.Source);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Concurrent access to cache — thread safety
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentAccess_ToGetMethods_DoesNotThrow()
    {
        var rt = CreateRuntime();

        var tasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                rt.Get("timeout");
                rt.GetString("timeout");
                rt.GetInt("timeout");
                rt.GetBool("enabled");
                rt.Exists("timeout");
                rt.GetAll();
            }));
        }

        await Task.WhenAll(tasks);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged — empty changes array
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigChanged_EmptyChangesArray_NoChange()
    {
        var events = new List<ConfigChangeEvent>();
        var rt = CreateRuntime();
        rt.OnChange(evt => events.Add(evt));

        var changesJson = """
        {
            "type": "config_changed",
            "config_id": "config-1",
            "changes": []
        }
        """;

        using var doc = JsonDocument.Parse(changesJson);
        InvokePrivate(rt, "HandleConfigChanged", doc.RootElement);

        Assert.Empty(events);
        Assert.Equal(60, rt.Get("timeout")); // unchanged

        await rt.CloseAsync();
    }
}
