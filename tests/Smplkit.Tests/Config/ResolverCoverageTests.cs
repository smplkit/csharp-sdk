using System.Text.Json;
using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Additional Resolver tests for 100% code coverage.
/// Targets edge cases in normalization and deep merge.
/// </summary>
public class ResolverCoverageTests
{
    // ------------------------------------------------------------------
    // NormalizeJsonElement — number that doesn't fit in Int64 (returns double)
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_JsonElement_LargeDouble_ReturnsDouble()
    {
        // A number larger than Int64.MaxValue that can't be represented as Int64
        var je = JsonDocument.Parse("1.7976931348623157E+308").RootElement;
        var result = Resolver.Normalize(je);
        Assert.IsType<double>(result);
    }

    // ------------------------------------------------------------------
    // NormalizeJsonElement — negative double returns double
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_JsonElement_NegativeDouble_ReturnsDouble()
    {
        var je = JsonDocument.Parse("-3.14").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal(-3.14, result);
    }

    // ------------------------------------------------------------------
    // NormalizeJsonElement — zero returns long
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_JsonElement_Zero_ReturnsLong()
    {
        var je = JsonDocument.Parse("0").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal(0L, result);
    }

    // ------------------------------------------------------------------
    // NormalizeJsonElement — negative int returns long
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_JsonElement_NegativeInt_ReturnsLong()
    {
        var je = JsonDocument.Parse("-42").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal(-42L, result);
    }

    // ------------------------------------------------------------------
    // NormalizeDict — dict with null values
    // ------------------------------------------------------------------

    [Fact]
    public void NormalizeDict_WithNullValue_PreservesNull()
    {
        var dict = new Dictionary<string, object?> { ["key"] = null };
        var result = Resolver.NormalizeDict(dict);
        Assert.True(result.ContainsKey("key"));
        Assert.Null(result["key"]);
    }

    // ------------------------------------------------------------------
    // NormalizeDict — dict with nested dict
    // ------------------------------------------------------------------

    [Fact]
    public void NormalizeDict_WithNestedDict_NormalizesRecursively()
    {
        var inner = new Dictionary<string, object?> { ["inner_key"] = "inner_val" };
        var dict = new Dictionary<string, object?> { ["nested"] = inner };
        var result = Resolver.NormalizeDict(dict);

        var nested = Assert.IsType<Dictionary<string, object?>>(result["nested"]);
        Assert.Equal("inner_val", nested["inner_key"]);
    }

    // ------------------------------------------------------------------
    // Normalize — already-normalized dict passes through
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_AlreadyNormalizedDict_ReturnsCopy()
    {
        var dict = new Dictionary<string, object?> { ["a"] = 1, ["b"] = "two" };
        var result = Resolver.Normalize(dict);
        var normalized = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(1, normalized["a"]);
        Assert.Equal("two", normalized["b"]);
    }

    // ------------------------------------------------------------------
    // Normalize — non-dict non-JsonElement values pass through
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_IntValue_PassesThrough()
    {
        Assert.Equal(42, Resolver.Normalize(42));
    }

    [Fact]
    public void Normalize_ArrayValue_PassesThrough()
    {
        var arr = new object?[] { 1, "two", true };
        var result = Resolver.Normalize(arr);
        // Non-JsonElement, non-dict passes through unchanged
        Assert.Same(arr, result);
    }

    // ------------------------------------------------------------------
    // DeepMerge — null values in override replace non-null base
    // ------------------------------------------------------------------

    [Fact]
    public void DeepMerge_NullOverrideReplacesDict()
    {
        var @base = new Dictionary<string, object?>
        {
            ["key"] = new Dictionary<string, object?> { ["inner"] = 1 }
        };
        var @override = new Dictionary<string, object?> { ["key"] = null };

        var result = Resolver.DeepMerge(@base, @override);
        Assert.Null(result["key"]);
    }

    // ------------------------------------------------------------------
    // Resolve — single entry with multiple env values
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_SingleEntry_MultiplEnvs_OnlyRequestedEnvApplied()
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
                    ["development"] = new() { ["timeout"] = 10 },
                },
            },
        };

        var result = Resolver.Resolve(chain, "staging");
        Assert.Equal(45, result["timeout"]);
    }

    // ------------------------------------------------------------------
    // ToChainEntry — config with empty values and empty environments
    // ------------------------------------------------------------------

    [Fact]
    public void ToChainEntry_EmptyConfig_ReturnsEmptyEntry()
    {
        var config = new Smplkit.Config.Config(
            Id: "id-1",
            Key: "key",
            Name: "Name",
            Description: null,
            Parent: null,
            Values: new(),
            Environments: new(),
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);
        Assert.Equal("id-1", entry.Id);
        Assert.Empty(entry.Values);
        Assert.Empty(entry.EnvValues);
    }

    // ------------------------------------------------------------------
    // ToChainEntry — config with JsonElement values in base
    // ------------------------------------------------------------------

    [Fact]
    public void ToChainEntry_JsonElementBaseValues_AreNormalized()
    {
        var je = JsonDocument.Parse("""{"nested": {"key": 42}}""").RootElement;
        var config = new Smplkit.Config.Config(
            Id: "id-1",
            Key: "key",
            Name: "Name",
            Description: null,
            Parent: null,
            Values: new() { ["complex"] = je },
            Environments: new(),
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);
        var complex = Assert.IsType<Dictionary<string, object?>>(entry.Values["complex"]);
        var nested = Assert.IsType<Dictionary<string, object?>>(complex["nested"]);
        Assert.Equal(42L, nested["key"]);
    }

    // ------------------------------------------------------------------
    // Resolve — deep merge across chain levels with nested dicts
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_NestedDicts_DeepMergedAcrossChain()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new()
                {
                    ["db"] = new Dictionary<string, object?>
                    {
                        ["host"] = "child-host",
                    },
                },
                EnvValues = new(),
            },
            new()
            {
                Id = "parent",
                Values = new()
                {
                    ["db"] = new Dictionary<string, object?>
                    {
                        ["host"] = "parent-host",
                        ["port"] = 5432,
                    },
                },
                EnvValues = new(),
            },
        };

        var result = Resolver.Resolve(chain, "any");
        var db = Assert.IsType<Dictionary<string, object?>>(result["db"]);
        Assert.Equal("child-host", db["host"]); // child wins
        Assert.Equal(5432, db["port"]);          // inherited from parent
    }

    // ------------------------------------------------------------------
    // NormalizeJsonElement — nested array inside object
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_JsonElement_ObjectWithArray_NormalizesAll()
    {
        var je = JsonDocument.Parse("""{"tags": ["a", "b"], "count": 2}""").RootElement;
        var result = Resolver.Normalize(je);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var tags = Assert.IsType<object?[]>(dict["tags"]);
        Assert.Equal("a", tags[0]);
        Assert.Equal("b", tags[1]);
        Assert.Equal(2L, dict["count"]);
    }

    // ------------------------------------------------------------------
    // DeepMerge — disjoint keys
    // ------------------------------------------------------------------

    [Fact]
    public void DeepMerge_DisjointKeys_CombinesAll()
    {
        var @base = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 };
        var @override = new Dictionary<string, object?> { ["c"] = 3, ["d"] = 4 };
        var result = Resolver.DeepMerge(@base, @override);
        Assert.Equal(4, result.Count);
        Assert.Equal(1, result["a"]);
        Assert.Equal(2, result["b"]);
        Assert.Equal(3, result["c"]);
        Assert.Equal(4, result["d"]);
    }

    // ------------------------------------------------------------------
    // Resolve — env values with nested dict override
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_EnvWithNestedDict_OverridesCorrectly()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new()
                {
                    ["db"] = new Dictionary<string, object?>
                    {
                        ["host"] = "localhost",
                        ["port"] = 5432,
                    },
                },
                EnvValues = new()
                {
                    ["production"] = new()
                    {
                        ["db"] = new Dictionary<string, object?>
                        {
                            ["host"] = "prod-db.example.com",
                        },
                    },
                },
            },
        };

        var result = Resolver.Resolve(chain, "production");
        var db = Assert.IsType<Dictionary<string, object?>>(result["db"]);
        Assert.Equal("prod-db.example.com", db["host"]); // env override
        Assert.Equal(5432, db["port"]);                    // from base
    }

    // ------------------------------------------------------------------
    // ToChainEntry — env with JsonElement array value
    // ------------------------------------------------------------------

    [Fact]
    public void ToChainEntry_EnvWithJsonElementValues_Normalizes()
    {
        var envData = new Dictionary<string, object?>
        {
            ["values"] = JsonDocument.Parse("""{"tags": ["a", "b"]}""").RootElement,
        };

        var config = new Smplkit.Config.Config(
            Id: "id-1",
            Key: "key",
            Name: "Name",
            Description: null,
            Parent: null,
            Values: new(),
            Environments: new()
            {
                ["production"] = envData,
            },
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);
        Assert.True(entry.EnvValues.ContainsKey("production"));
        var tags = Assert.IsType<object?[]>(entry.EnvValues["production"]["tags"]);
        Assert.Equal("a", tags[0]);
        Assert.Equal("b", tags[1]);
    }
}
