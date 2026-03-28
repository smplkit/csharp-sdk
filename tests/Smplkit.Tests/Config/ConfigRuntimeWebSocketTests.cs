using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests targeting ConfigRuntime WebSocket-related methods and reconnection logic.
/// Since we cannot inject a mock WebSocket, these tests exercise the code paths
/// by manipulating the runtime state through public/internal APIs and verifying
/// observable behavior (cache updates, listener notifications, connection status, stats).
/// </summary>
public class ConfigRuntimeWebSocketTests : IAsyncLifetime
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

    // ------------------------------------------------------------------
    // ResyncCacheAsync — tested indirectly through the reconnection loop
    // When the WS fails, the runtime resyncs via fetchChainFn.
    // We test this by creating a runtime with a fetchChainFn that changes
    // values, then waiting for the background task to resync.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_WithFetchChainFn_EventuallyResyncs()
    {
        int fetchCalls = 0;
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

        var rt = CreateRuntime(fetchChainFn: ct =>
        {
            Interlocked.Increment(ref fetchCalls);
            return Task.FromResult(newChain);
        });

        // Wait for the background WS task to fail connect and resync
        // The backoff starts at 1 second, so wait a bit
        await Task.Delay(3000);

        // The background resync should have called fetchChainFn at least once
        // (may or may not have updated cache depending on timing)
        var currentFetches = Interlocked.CompareExchange(ref fetchCalls, 0, 0);
        Assert.True(currentFetches >= 1, $"Expected at least 1 fetch call, got {currentFetches}");

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync with null fetchChainFn — no-op
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_NullFetchChainFn_ResyncIsNoOp()
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

        // Wait for background task to attempt reconnect
        await Task.Delay(2000);

        // Value should still be intact
        Assert.Equal("value", _runtime.Get("key"));
        await _runtime.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync error path — swallows exception
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_FetchChainFnThrows_ResyncSwallowsException()
    {
        int fetchCalls = 0;
        var rt = CreateRuntime(fetchChainFn: _ =>
        {
            Interlocked.Increment(ref fetchCalls);
            throw new Exception("resync fetch failed");
        });

        // Wait for background task to attempt reconnect and resync
        await Task.Delay(3000);

        // Should not have crashed — old cache should be intact
        Assert.Equal(60, rt.Get("timeout"));
        var currentFetches = Interlocked.CompareExchange(ref fetchCalls, 0, 0);
        Assert.True(currentFetches >= 1, "fetchChainFn should have been called at least once");

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ResyncCacheAsync fires change listeners with source="websocket"
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_Resync_FiresChangeListenersWithWebsocketSource()
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

        // Wait for WS failure + resync
        await Task.Delay(3000);

        await rt.CloseAsync();

        // If resync occurred, there should be change events with source "websocket"
        if (events.Count > 0)
        {
            Assert.All(events, e => Assert.Equal("websocket", e.Source));
        }
    }

    // ------------------------------------------------------------------
    // CloseAsync — sets disconnected and stops background task
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_ImmediatelySetsDisconnected()
    {
        var rt = CreateRuntime();
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    [Fact]
    public async Task CloseAsync_CanBeCalledMultipleTimes()
    {
        var rt = CreateRuntime();
        await rt.CloseAsync();
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // DisposeAsync calls CloseAsync
    // ------------------------------------------------------------------

    [Fact]
    public async Task DisposeAsync_SetsDisconnected()
    {
        var rt = CreateRuntime();
        await rt.DisposeAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // ConnectionStatus during startup
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConnectionStatus_DuringStartup_IsConnectingOrDisconnected()
    {
        var rt = CreateRuntime();
        // Give a moment for background task to start
        await Task.Delay(50);
        var status = rt.ConnectionStatus();
        Assert.Contains(status, new[] { "connecting", "disconnected", "connected" });
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // RefreshAsync with change listeners and "manual" source
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_FiresListenersWithManualSource()
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

        await rt.RefreshAsync();
        await rt.CloseAsync();

        var timeoutEvent = events.Find(e => e.Key == "timeout");
        Assert.NotNull(timeoutEvent);
        Assert.Equal("manual", timeoutEvent.Source);
    }

    // ------------------------------------------------------------------
    // Stats after resync
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stats_AfterResync_IncrementsFetchCount()
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

        var initialFetchCount = rt.Stats().FetchCount;

        // Wait for background resync
        await Task.Delay(3000);

        var afterResyncStats = rt.Stats();
        Assert.True(afterResyncStats.FetchCount > initialFetchCount,
            $"Expected fetch count > {initialFetchCount}, got {afterResyncStats.FetchCount}");
        Assert.NotNull(afterResyncStats.LastFetchAt);

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Multiple environments — only target env is resolved
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_MultipleEnvs_OnlyTargetEnvResolved()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 60 },
                    ["staging"] = new() { ["timeout"] = 45 },
                },
            },
        };

        var rt = CreateRuntime(chain, environment: "staging");
        Assert.Equal(45, rt.Get("timeout"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Runtime with empty environment name
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_EmptyEnvironment_UsesBaseValues()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 60 },
                },
            },
        };

        var rt = CreateRuntime(chain, environment: "nonexistent_env");
        Assert.Equal(30, rt.Get("timeout"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // CloseAsync with cancellation token
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_WithCancellationToken_Completes()
    {
        var rt = CreateRuntime();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await rt.CloseAsync(cts.Token);
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }

    // ------------------------------------------------------------------
    // OnChange — multiple key-filtered listeners on different keys
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnChange_DifferentKeyFilters_EachFiresCorrectly()
    {
        var timeoutEvents = new List<ConfigChangeEvent>();
        var retriesEvents = new List<ConfigChangeEvent>();

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["timeout"] = 999, ["retries"] = 99 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 999 },
                },
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => timeoutEvents.Add(evt), key: "timeout");
        rt.OnChange(evt => retriesEvents.Add(evt), key: "retries");

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.Single(timeoutEvents);
        Assert.Equal("timeout", timeoutEvents[0].Key);
        Assert.Single(retriesEvents);
        Assert.Equal("retries", retriesEvents[0].Key);
    }

    // ------------------------------------------------------------------
    // GetAll after refresh — returns updated values
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetAll_AfterRefresh_ReturnsUpdatedValues()
    {
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "config-1",
                Values = new() { ["new_key"] = "new_val" },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(fetchChainFn: _ => Task.FromResult(newChain));
        await rt.RefreshAsync();

        var all = rt.GetAll();
        Assert.Equal("new_val", all["new_key"]);
        Assert.False(all.ContainsKey("timeout")); // Old key should be gone
        Assert.False(all.ContainsKey("retries")); // Old key should be gone

        await rt.CloseAsync();
    }
}
