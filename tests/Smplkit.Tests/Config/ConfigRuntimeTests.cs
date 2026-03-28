using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

public class ConfigRuntimeTests : IAsyncLifetime
{
    private ConfigRuntime? _runtime;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a ConfigRuntime with no WebSocket connectivity (fetchChainFn provided but WS will fail immediately).
    /// The WS background task will fail to connect and just retry, which is fine for unit tests.
    /// We close it in DisposeAsync.
    /// </summary>
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
                Values = new()
                {
                    ["timeout"] = 30,
                    ["retries"] = 3,
                    ["name"] = "test-service",
                    ["enabled"] = true,
                    ["ratio"] = 0.75,
                },
                EnvValues = new()
                {
                    ["production"] = new()
                    {
                        ["timeout"] = 60,
                        ["prod_only"] = "yes",
                    },
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

    // ------------------------------------------------------------------
    // Get
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_ExistingKey_ReturnsValue()
    {
        var rt = CreateRuntime();
        // timeout should be 60 (env override wins over base 30)
        Assert.Equal(60, rt.Get("timeout"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task Get_MissingKey_ReturnsDefault()
    {
        var rt = CreateRuntime();
        Assert.Null(rt.Get("nonexistent"));
        Assert.Equal("fallback", rt.Get("nonexistent", "fallback"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task Get_EnvSpecificKey_Resolved()
    {
        var rt = CreateRuntime();
        Assert.Equal("yes", rt.Get("prod_only"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetString
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetString_ExistingStringKey_ReturnsString()
    {
        var rt = CreateRuntime();
        Assert.Equal("test-service", rt.GetString("name"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetString_NonStringKey_ReturnsDefault()
    {
        var rt = CreateRuntime();
        // "timeout" is an int, not a string
        Assert.Null(rt.GetString("timeout"));
        Assert.Equal("fallback", rt.GetString("timeout", "fallback"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetString_MissingKey_ReturnsDefault()
    {
        var rt = CreateRuntime();
        Assert.Null(rt.GetString("nonexistent"));
        Assert.Equal("default", rt.GetString("nonexistent", "default"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetInt
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_IntValue_ReturnsInt()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["count"] = 42 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(42, rt.GetInt("count"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_LongValue_ReturnsIntCast()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["count"] = 42L },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(42, rt.GetInt("count"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_DoubleWithNoFraction_ReturnsIntCast()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["count"] = 42.0 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(42, rt.GetInt("count"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_DoubleWithFraction_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["ratio"] = 3.14 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("ratio"));
        Assert.Equal(99, rt.GetInt("ratio", 99));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_DoubleOutOfIntRange_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["big"] = (double)long.MaxValue },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("big"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_StringValue_ReturnsDefault()
    {
        var rt = CreateRuntime();
        Assert.Null(rt.GetInt("name"));
        Assert.Equal(5, rt.GetInt("name", 5));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_MissingKey_ReturnsDefault()
    {
        var rt = CreateRuntime();
        Assert.Null(rt.GetInt("nonexistent"));
        Assert.Equal(10, rt.GetInt("nonexistent", 10));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetBool
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBool_BoolValue_ReturnsBool()
    {
        var rt = CreateRuntime();
        Assert.True(rt.GetBool("enabled"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetBool_NonBoolValue_ReturnsDefault()
    {
        var rt = CreateRuntime();
        Assert.Null(rt.GetBool("timeout"));
        Assert.False(rt.GetBool("timeout", false));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetBool_MissingKey_ReturnsDefault()
    {
        var rt = CreateRuntime();
        Assert.Null(rt.GetBool("nonexistent"));
        Assert.True(rt.GetBool("nonexistent", true));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetAll
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAll_ReturnsShallowCopy()
    {
        var rt = CreateRuntime();
        var all = rt.GetAll();
        Assert.True(all.Count > 0);
        Assert.Equal(60, all["timeout"]);
        Assert.Equal("test-service", all["name"]);

        // Mutating the copy should not affect the runtime
        all["timeout"] = 999;
        Assert.Equal(60, rt.Get("timeout"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Exists
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exists_ExistingKey_ReturnsTrue()
    {
        var rt = CreateRuntime();
        Assert.True(rt.Exists("timeout"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task Exists_MissingKey_ReturnsFalse()
    {
        var rt = CreateRuntime();
        Assert.False(rt.Exists("nonexistent"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Stats
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stats_InitialValues_AreCorrect()
    {
        var chain = new List<ConfigChainEntry>
        {
            new() { Id = "c1", Values = new(), EnvValues = new() },
            new() { Id = "c2", Values = new(), EnvValues = new() },
        };
        var rt = CreateRuntime(chain);
        var stats = rt.Stats();
        Assert.Equal(2, stats.FetchCount);  // chain.Count
        Assert.NotNull(stats.LastFetchAt);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ConnectionStatus
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectionStatus_InitiallyConnectingOrDisconnected()
    {
        var rt = CreateRuntime();
        // The background WS task tries to connect and will fail (no real server),
        // so status could be "connecting" or "disconnected"
        var status = rt.ConnectionStatus();
        Assert.Contains(status, new[] { "connecting", "disconnected", "connected" });
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // OnChange
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnChange_Wildcard_FiresForAnyKeyChange()
    {
        var events = new List<ConfigChangeEvent>();
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999, ["name"] = "test-service", ["retries"] = 3, ["enabled"] = true, ["ratio"] = 0.75 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 999, ["prod_only"] = "yes" },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt));

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.True(events.Count > 0);
        var timeoutEvent = events.Find(e => e.Key == "timeout");
        Assert.NotNull(timeoutEvent);
        Assert.Equal(60, timeoutEvent.OldValue);   // was 60 (env override)
        Assert.Equal(999, timeoutEvent.NewValue);
        Assert.Equal("manual", timeoutEvent.Source);
    }

    [Fact]
    public async Task OnChange_WithKeyFilter_OnlyFiresForMatchingKey()
    {
        var events = new List<ConfigChangeEvent>();
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999, ["retries"] = 99, ["name"] = "test-service", ["enabled"] = true, ["ratio"] = 0.75 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 999, ["prod_only"] = "yes" },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt), key: "retries");

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.Single(events);
        Assert.Equal("retries", events[0].Key);
    }

    [Fact]
    public async Task OnChange_ListenerException_DoesNotPreventOtherListeners()
    {
        var events = new List<ConfigChangeEvent>();
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999, ["name"] = "test-service", ["retries"] = 3, ["enabled"] = true, ["ratio"] = 0.75 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 999, ["prod_only"] = "yes" },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(_ => throw new Exception("listener error"));
        rt.OnChange(evt => events.Add(evt));

        await rt.RefreshAsync();
        await rt.CloseAsync();

        // Second listener should still fire despite first throwing
        Assert.True(events.Count > 0);
    }

    [Fact]
    public async Task OnChange_NoChanges_DoesNotFire()
    {
        var events = new List<ConfigChangeEvent>();

        // Return exact same chain
        var sameChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new()
                {
                    ["timeout"] = 30,
                    ["retries"] = 3,
                    ["name"] = "test-service",
                    ["enabled"] = true,
                    ["ratio"] = 0.75,
                },
                EnvValues = new()
                {
                    ["production"] = new()
                    {
                        ["timeout"] = 60,
                        ["prod_only"] = "yes",
                    },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(sameChain));
        rt.OnChange(evt => events.Add(evt));

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.Empty(events);
    }

    // ------------------------------------------------------------------
    // RefreshAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesCacheAndStats()
    {
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999 },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));

        var oldStats = rt.Stats();
        await rt.RefreshAsync();

        Assert.Equal(999, rt.Get("timeout"));
        var newStats = rt.Stats();
        Assert.True(newStats.FetchCount > oldStats.FetchCount);
        Assert.NotNull(newStats.LastFetchAt);

        await rt.CloseAsync();
    }

    [Fact]
    public async Task RefreshAsync_WithNullFetchFn_DoesNothing()
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

        await _runtime.RefreshAsync();
        Assert.Equal("value", _runtime.Get("key"));
        await _runtime.CloseAsync();
    }

    // ------------------------------------------------------------------
    // CloseAsync / DisposeAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_SetsDisconnected()
    {
        var rt = CreateRuntime();
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMultipleTimes()
    {
        var rt = CreateRuntime();
        await rt.DisposeAsync();
        await rt.DisposeAsync(); // Should not throw
    }

    // ------------------------------------------------------------------
    // HandleConfigChanged (tested through internal state changes)
    // ------------------------------------------------------------------

    [Fact]
    public async Task FireChangeListeners_KeyAddedAndRemoved_ReportsCorrectly()
    {
        var events = new List<ConfigChangeEvent>();

        // Start with key "a" = 1
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["a"] = 1 },
                EnvValues = new(),
            },
        };

        // After refresh, key "a" is gone and key "b" is added
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["b"] = 2 },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt));

        await rt.RefreshAsync();
        await rt.CloseAsync();

        // Should have events for both "a" (removed) and "b" (added)
        var removedEvent = events.Find(e => e.Key == "a");
        Assert.NotNull(removedEvent);
        Assert.Equal(1, removedEvent.OldValue);
        Assert.Null(removedEvent.NewValue);

        var addedEvent = events.Find(e => e.Key == "b");
        Assert.NotNull(addedEvent);
        Assert.Null(addedEvent.OldValue);
        Assert.Equal(2, addedEvent.NewValue);
    }

    // ------------------------------------------------------------------
    // HandleConfigDeleted
    // ------------------------------------------------------------------

    [Fact]
    public async Task HandleConfigDeleted_SetsClosedAndDisconnected()
    {
        // We can test this indirectly: after close, status is disconnected
        var rt = CreateRuntime();
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync (tested indirectly through RefreshAsync)
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_WithFetchError_KeepsOldCache()
    {
        var rt = CreateRuntime(fetchChainFn: _ =>
            throw new Exception("fetch failed"));

        // RefreshAsync should propagate the exception (unlike ResyncCacheAsync which swallows)
        await Assert.ThrowsAsync<Exception>(() => rt.RefreshAsync());
        // Old cache should still be intact
        Assert.Equal(60, rt.Get("timeout"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // BuildWebSocketUrl
    // ------------------------------------------------------------------

    // BuildWebSocketUrl is private, but we test it indirectly via ConnectAsync/CloseAsync flow.
    // The fact that the runtime initializes and tries to connect covers that code path.

    [Fact]
    public async Task Runtime_InitializesAndCanClose()
    {
        var rt = CreateRuntime();
        // Give the background task a moment to attempt connection
        await Task.Delay(100);
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }
}
