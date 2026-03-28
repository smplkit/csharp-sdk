using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Additional edge case tests for ConfigRuntime to maximize code coverage.
/// </summary>
public class ConfigRuntimeEdgeCaseTests : IAsyncLifetime
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
        List<ConfigChainEntry> chain,
        string environment = "production",
        Func<CancellationToken, Task<List<ConfigChainEntry>>>? fetchChainFn = null)
    {
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
    // GetInt edge cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_DoubleMinIntValue_ReturnsInt()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["min"] = (double)int.MinValue },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(int.MinValue, rt.GetInt("min"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_DoubleMaxIntValue_ReturnsInt()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["max"] = (double)int.MaxValue },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(int.MaxValue, rt.GetInt("max"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_NullValue_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["nullable"] = null },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("nullable"));
        Assert.Equal(42, rt.GetInt("nullable", 42));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_BoolValue_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["flag"] = true },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("flag"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetBool edge cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBool_FalseValue_ReturnsFalse()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["disabled"] = false },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.False(rt.GetBool("disabled"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetBool_NullValue_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["nullable"] = null },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetBool("nullable"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetString edge cases
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetString_NullValue_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["nullable"] = null },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetString("nullable"));
        Assert.Equal("def", rt.GetString("nullable", "def"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetString_EmptyString_ReturnsEmptyString()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["empty"] = "" },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal("", rt.GetString("empty"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Get with null value in cache
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_NullValueInCache_ReturnsNull()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["nullable"] = null },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        // Key exists with null value — should return null, not the default
        Assert.Null(rt.Get("nullable", "default_val"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Exists with null value
    // ------------------------------------------------------------------

    [Fact]
    public async Task Exists_NullValue_ReturnsTrue()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["nullable"] = null },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.True(rt.Exists("nullable"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // RefreshAsync updates stats correctly
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_MultipleTimes_AccumulatesFetchCount()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["key"] = "val" },
                EnvValues = new(),
            },
        };

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["key"] = "val" },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, fetchChainFn: _ => Task.FromResult(newChain));

        var initialCount = rt.Stats().FetchCount; // Should be 1 (chain.Count)
        await rt.RefreshAsync();
        var afterFirst = rt.Stats().FetchCount;
        await rt.RefreshAsync();
        var afterSecond = rt.Stats().FetchCount;

        Assert.Equal(1, initialCount);
        Assert.Equal(2, afterFirst);    // +1 from refresh
        Assert.Equal(3, afterSecond);   // +1 from second refresh

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // OnChange with multiple listeners
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnChange_MultipleWildcardListeners_AllFire()
    {
        var events1 = new List<ConfigChangeEvent>();
        var events2 = new List<ConfigChangeEvent>();

        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["key"] = "old" },
                EnvValues = new(),
            },
        };

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["key"] = "new" },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events1.Add(evt));
        rt.OnChange(evt => events2.Add(evt));

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.Single(events1);
        Assert.Single(events2);
        Assert.Equal("old", events1[0].OldValue);
        Assert.Equal("new", events1[0].NewValue);
    }

    [Fact]
    public async Task OnChange_FilteredListener_DoesNotFireForOtherKeys()
    {
        var events = new List<ConfigChangeEvent>();

        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["a"] = 1, ["b"] = 2 },
                EnvValues = new(),
            },
        };

        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["a"] = 99, ["b"] = 99 },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => events.Add(evt), key: "a");

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.Single(events);
        Assert.Equal("a", events[0].Key);
    }

    // ------------------------------------------------------------------
    // Empty chain
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_WithEmptyChain_HasEmptyCache()
    {
        var rt = CreateRuntime(new List<ConfigChainEntry>());
        Assert.Empty(rt.GetAll());
        Assert.False(rt.Exists("anything"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Multi-entry chain with environment overrides
    // ------------------------------------------------------------------

    [Fact]
    public async Task Runtime_MultiEntryChain_ResolvesCorrectly()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["child_val"] = "c" },
                EnvValues = new()
                {
                    ["prod"] = new() { ["env_val"] = "prod_c" },
                },
            },
            new()
            {
                Id = "parent",
                Values = new() { ["parent_val"] = "p", ["child_val"] = "parent_override" },
                EnvValues = new()
                {
                    ["prod"] = new() { ["parent_env"] = "parent_prod" },
                },
            },
        };

        var rt = CreateRuntime(chain, "prod");
        Assert.Equal("c", rt.Get("child_val"));               // child wins
        Assert.Equal("p", rt.Get("parent_val"));               // inherited
        Assert.Equal("prod_c", rt.Get("env_val"));             // child env
        Assert.Equal("parent_prod", rt.Get("parent_env"));     // parent env

        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Stats after initialization
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stats_FetchCount_EqualsChainLength()
    {
        var chain = new List<ConfigChainEntry>
        {
            new() { Id = "a", Values = new(), EnvValues = new() },
            new() { Id = "b", Values = new(), EnvValues = new() },
            new() { Id = "c", Values = new(), EnvValues = new() },
        };

        var rt = CreateRuntime(chain);
        Assert.Equal(3, rt.Stats().FetchCount);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // ConnectionStatus after close
    // ------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_ThenConnectionStatus_ReturnsDisconnected()
    {
        var chain = new List<ConfigChainEntry>
        {
            new() { Id = "c1", Values = new(), EnvValues = new() },
        };
        var rt = CreateRuntime(chain);
        await rt.CloseAsync();
        Assert.Equal("disconnected", rt.ConnectionStatus());
    }
}
