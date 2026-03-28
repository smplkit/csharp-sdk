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

    // ------------------------------------------------------------------
    // GetInt — double exactly at int.MinValue boundary
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_DoubleJustBelowMinInt_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["below_min"] = (double)int.MinValue - 1.0 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("below_min"));
        await rt.CloseAsync();
    }

    [Fact]
    public async Task GetInt_DoubleJustAboveMaxInt_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["above_max"] = (double)int.MaxValue + 1.0 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetInt("above_max"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetInt — negative integer values
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_NegativeInt_ReturnsCorrectValue()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["neg"] = -42 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(-42, rt.GetInt("neg"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetInt — negative long
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_NegativeLong_CastsToInt()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["neg_long"] = -100L },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(-100, rt.GetInt("neg_long"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetBool — string value returns default
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetBool_StringValue_ReturnsDefault()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["flag"] = "true" },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Null(rt.GetBool("flag"));
        Assert.True(rt.GetBool("flag", true));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetString — integer value returns default
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetString_IntValue_ReturnsDefault()
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
        Assert.Null(rt.GetString("count"));
        Assert.Equal("default", rt.GetString("count", "default"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetString — bool value returns default
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetString_BoolValue_ReturnsDefault()
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
        Assert.Null(rt.GetString("flag"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Get — returns null value (key exists with null)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Get_NullValue_WithDefault_ReturnsNull()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["key"] = null },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        // The TryGetValue succeeds, so null is returned (not the default)
        Assert.Null(rt.Get("key", "fallback"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // OnChange — wildcard and key-filtered both fire
    // ------------------------------------------------------------------

    [Fact]
    public async Task OnChange_WildcardAndKeyFilter_BothFire()
    {
        var wildcardEvents = new List<ConfigChangeEvent>();
        var keyEvents = new List<ConfigChangeEvent>();

        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["a"] = 1 },
                EnvValues = new(),
            },
        };
        var newChain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["a"] = 2 },
                EnvValues = new(),
            },
        };

        var rt = CreateRuntime(chain, fetchChainFn: _ => Task.FromResult(newChain));
        rt.OnChange(evt => wildcardEvents.Add(evt));
        rt.OnChange(evt => keyEvents.Add(evt), key: "a");

        await rt.RefreshAsync();
        await rt.CloseAsync();

        Assert.Single(wildcardEvents);
        Assert.Single(keyEvents);
    }

    // ------------------------------------------------------------------
    // Stats timestamp is ISO-8601
    // ------------------------------------------------------------------

    [Fact]
    public async Task Stats_LastFetchAt_IsIso8601Format()
    {
        var chain = new List<ConfigChainEntry>
        {
            new() { Id = "c1", Values = new(), EnvValues = new() },
        };
        var rt = CreateRuntime(chain);
        var stats = rt.Stats();
        Assert.NotNull(stats.LastFetchAt);
        // Verify it's parseable as ISO-8601
        Assert.True(DateTimeOffset.TryParse(stats.LastFetchAt, out _));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // Refresh updates _lastFetchAt
    // ------------------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesLastFetchAt()
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
        var before = rt.Stats().LastFetchAt;
        await Task.Delay(10); // tiny delay to ensure timestamp differs
        await rt.RefreshAsync();
        var after = rt.Stats().LastFetchAt;

        Assert.NotEqual(before, after);
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetInt — double value of exactly 0.0 returns 0
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_DoubleZero_ReturnsZero()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["zero"] = 0.0 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(0, rt.GetInt("zero"));
        await rt.CloseAsync();
    }

    // ------------------------------------------------------------------
    // GetInt — double value of -0.0 returns 0
    // ------------------------------------------------------------------

    [Fact]
    public async Task GetInt_NegativeDoubleZero_ReturnsZero()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["negzero"] = -0.0 },
                EnvValues = new(),
            },
        };
        var rt = CreateRuntime(chain);
        Assert.Equal(0, rt.GetInt("negzero"));
        await rt.CloseAsync();
    }
}
