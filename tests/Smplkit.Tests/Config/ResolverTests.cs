using System.Text.Json;
using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

public class ResolverTests
{
    // ------------------------------------------------------------------
    // DeepMerge
    // ------------------------------------------------------------------

    [Fact]
    public void DeepMerge_EmptyBothDicts_ReturnsEmpty()
    {
        var result = Resolver.DeepMerge(new(), new());
        Assert.Empty(result);
    }

    [Fact]
    public void DeepMerge_EmptyBase_ReturnsOverride()
    {
        var @override = new Dictionary<string, object?> { ["key"] = "value" };
        var result = Resolver.DeepMerge(new(), @override);
        Assert.Equal("value", result["key"]);
    }

    [Fact]
    public void DeepMerge_EmptyOverride_ReturnsBase()
    {
        var @base = new Dictionary<string, object?> { ["key"] = "value" };
        var result = Resolver.DeepMerge(@base, new());
        Assert.Equal("value", result["key"]);
    }

    [Fact]
    public void DeepMerge_OverrideReplacesBaseValue()
    {
        var @base = new Dictionary<string, object?> { ["key"] = "old" };
        var @override = new Dictionary<string, object?> { ["key"] = "new" };
        var result = Resolver.DeepMerge(@base, @override);
        Assert.Equal("new", result["key"]);
    }

    [Fact]
    public void DeepMerge_OverrideAddsNewKey()
    {
        var @base = new Dictionary<string, object?> { ["a"] = 1 };
        var @override = new Dictionary<string, object?> { ["b"] = 2 };
        var result = Resolver.DeepMerge(@base, @override);
        Assert.Equal(1, result["a"]);
        Assert.Equal(2, result["b"]);
    }

    [Fact]
    public void DeepMerge_NestedDictsAreMergedRecursively()
    {
        var @base = new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?>
            {
                ["a"] = 1,
                ["b"] = 2,
            }
        };
        var @override = new Dictionary<string, object?>
        {
            ["nested"] = new Dictionary<string, object?>
            {
                ["b"] = 99,
                ["c"] = 3,
            }
        };

        var result = Resolver.DeepMerge(@base, @override);
        var nested = Assert.IsType<Dictionary<string, object?>>(result["nested"]);
        Assert.Equal(1, nested["a"]);
        Assert.Equal(99, nested["b"]);
        Assert.Equal(3, nested["c"]);
    }

    [Fact]
    public void DeepMerge_NonDictOverrideReplacesDictBase()
    {
        var @base = new Dictionary<string, object?>
        {
            ["key"] = new Dictionary<string, object?> { ["a"] = 1 }
        };
        var @override = new Dictionary<string, object?> { ["key"] = "flat_value" };

        var result = Resolver.DeepMerge(@base, @override);
        Assert.Equal("flat_value", result["key"]);
    }

    [Fact]
    public void DeepMerge_DictOverrideReplacesNonDictBase()
    {
        var @base = new Dictionary<string, object?> { ["key"] = "flat_value" };
        var @override = new Dictionary<string, object?>
        {
            ["key"] = new Dictionary<string, object?> { ["a"] = 1 }
        };

        var result = Resolver.DeepMerge(@base, @override);
        var nested = Assert.IsType<Dictionary<string, object?>>(result["key"]);
        Assert.Equal(1, nested["a"]);
    }

    [Fact]
    public void DeepMerge_NullValuesArePreserved()
    {
        var @base = new Dictionary<string, object?> { ["key"] = "value" };
        var @override = new Dictionary<string, object?> { ["key"] = null };

        var result = Resolver.DeepMerge(@base, @override);
        Assert.Null(result["key"]);
    }

    // ------------------------------------------------------------------
    // Resolve
    // ------------------------------------------------------------------

    [Fact]
    public void Resolve_EmptyChain_ReturnsEmpty()
    {
        var result = Resolver.Resolve(new List<ConfigChainEntry>(), "production");
        Assert.Empty(result);
    }

    [Fact]
    public void Resolve_SingleEntry_ReturnsBaseValues()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new(),
            },
        };

        var result = Resolver.Resolve(chain, "production");
        Assert.Equal(30, result["timeout"]);
    }

    [Fact]
    public void Resolve_SingleEntry_EnvOverridesBase()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new()
                {
                    ["production"] = new() { ["timeout"] = 60 }
                },
            },
        };

        var result = Resolver.Resolve(chain, "production");
        Assert.Equal(60, result["timeout"]);
    }

    [Fact]
    public void Resolve_SingleEntry_DifferentEnvUsesBase()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new()
                {
                    ["staging"] = new() { ["timeout"] = 60 }
                },
            },
        };

        var result = Resolver.Resolve(chain, "production");
        Assert.Equal(30, result["timeout"]);
    }

    [Fact]
    public void Resolve_ChildFirstChain_ChildOverridesParent()
    {
        // Chain is child-first, root-last
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["timeout"] = 60, ["child_only"] = "yes" },
                EnvValues = new(),
            },
            new()
            {
                Id = "parent",
                Values = new() { ["timeout"] = 30, ["parent_only"] = "yes" },
                EnvValues = new(),
            },
        };

        var result = Resolver.Resolve(chain, "production");
        Assert.Equal(60, result["timeout"]);        // child wins
        Assert.Equal("yes", result["child_only"]);   // child-only key
        Assert.Equal("yes", result["parent_only"]);   // inherited from parent
    }

    [Fact]
    public void Resolve_ThreeLevelChain_MergesCorrectly()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "grandchild",
                Values = new() { ["gc"] = "gc_val" },
                EnvValues = new()
                {
                    ["prod"] = new() { ["gc_env"] = "gc_env_val" }
                },
            },
            new()
            {
                Id = "child",
                Values = new() { ["c"] = "c_val" },
                EnvValues = new(),
            },
            new()
            {
                Id = "root",
                Values = new() { ["r"] = "r_val", ["gc"] = "root_gc" },
                EnvValues = new(),
            },
        };

        var result = Resolver.Resolve(chain, "prod");
        Assert.Equal("gc_val", result["gc"]);      // grandchild wins over root
        Assert.Equal("gc_env_val", result["gc_env"]);
        Assert.Equal("c_val", result["c"]);
        Assert.Equal("r_val", result["r"]);
    }

    // ------------------------------------------------------------------
    // Normalize
    // ------------------------------------------------------------------

    [Fact]
    public void Normalize_NullValue_ReturnsNull()
    {
        Assert.Null(Resolver.Normalize(null));
    }

    [Fact]
    public void Normalize_PlainString_ReturnsSameString()
    {
        Assert.Equal("hello", Resolver.Normalize("hello"));
    }

    [Fact]
    public void Normalize_PlainInt_ReturnsSameInt()
    {
        Assert.Equal(42, Resolver.Normalize(42));
    }

    [Fact]
    public void Normalize_Dict_NormalizesRecursively()
    {
        var dict = new Dictionary<string, object?> { ["key"] = "value" };
        var result = Resolver.Normalize(dict);
        var normalized = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal("value", normalized["key"]);
    }

    [Fact]
    public void Normalize_JsonElement_String_ReturnsString()
    {
        var je = JsonDocument.Parse("\"hello\"").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Normalize_JsonElement_Int_ReturnsLong()
    {
        var je = JsonDocument.Parse("42").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal(42L, result);
    }

    [Fact]
    public void Normalize_JsonElement_Double_ReturnsDouble()
    {
        var je = JsonDocument.Parse("3.14").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal(3.14, result);
    }

    [Fact]
    public void Normalize_JsonElement_True_ReturnsTrue()
    {
        var je = JsonDocument.Parse("true").RootElement;
        Assert.Equal(true, Resolver.Normalize(je));
    }

    [Fact]
    public void Normalize_JsonElement_False_ReturnsFalse()
    {
        var je = JsonDocument.Parse("false").RootElement;
        Assert.Equal(false, Resolver.Normalize(je));
    }

    [Fact]
    public void Normalize_JsonElement_Null_ReturnsNull()
    {
        var je = JsonDocument.Parse("null").RootElement;
        Assert.Null(Resolver.Normalize(je));
    }

    [Fact]
    public void Normalize_JsonElement_Object_ReturnsDict()
    {
        var je = JsonDocument.Parse("""{"a": 1, "b": "two"}""").RootElement;
        var result = Resolver.Normalize(je);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Equal(1L, dict["a"]);
        Assert.Equal("two", dict["b"]);
    }

    [Fact]
    public void Normalize_JsonElement_Array_ReturnsObjectArray()
    {
        var je = JsonDocument.Parse("[1, 2, 3]").RootElement;
        var result = Resolver.Normalize(je);
        var arr = Assert.IsType<object?[]>(result);
        Assert.Equal(3, arr.Length);
        Assert.Equal(1L, arr[0]);
        Assert.Equal(2L, arr[1]);
        Assert.Equal(3L, arr[2]);
    }

    [Fact]
    public void Normalize_JsonElement_NestedObject_ReturnsNestedDict()
    {
        var je = JsonDocument.Parse("""{"nested": {"a": true}}""").RootElement;
        var result = Resolver.Normalize(je);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var nested = Assert.IsType<Dictionary<string, object?>>(dict["nested"]);
        Assert.Equal(true, nested["a"]);
    }

    // ------------------------------------------------------------------
    // NormalizeDict
    // ------------------------------------------------------------------

    [Fact]
    public void NormalizeDict_EmptyDict_ReturnsEmpty()
    {
        var result = Resolver.NormalizeDict(new());
        Assert.Empty(result);
    }

    [Fact]
    public void NormalizeDict_WithJsonElements_NormalizesAll()
    {
        var je = JsonDocument.Parse("42").RootElement;
        var dict = new Dictionary<string, object?> { ["num"] = je, ["str"] = "hello" };
        var result = Resolver.NormalizeDict(dict);
        Assert.Equal(42L, result["num"]);
        Assert.Equal("hello", result["str"]);
    }

    // ------------------------------------------------------------------
    // ToChainEntry
    // ------------------------------------------------------------------

    [Fact]
    public void ToChainEntry_BasicConfig_ReturnsEntry()
    {
        var config = new Config.Config(
            Id: "id-1",
            Key: "my_key",
            Name: "My Config",
            Description: null,
            Parent: null,
            Values: new() { ["timeout"] = 30 },
            Environments: new(),
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);

        Assert.Equal("id-1", entry.Id);
        Assert.Equal(30, entry.Values["timeout"]);
        Assert.Empty(entry.EnvValues);
    }

    [Fact]
    public void ToChainEntry_WithEnvironments_ExtractsEnvValues()
    {
        var envData = new Dictionary<string, object?>
        {
            ["values"] = JsonDocument.Parse("""{"retries": 5}""").RootElement,
        };

        var config = new Config.Config(
            Id: "id-1",
            Key: "my_key",
            Name: "My Config",
            Description: null,
            Parent: null,
            Values: new() { ["timeout"] = 30 },
            Environments: new()
            {
                ["production"] = envData,
            },
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);

        Assert.True(entry.EnvValues.ContainsKey("production"));
        Assert.Equal(5L, entry.EnvValues["production"]["retries"]);
    }

    [Fact]
    public void ToChainEntry_EnvWithoutValuesKey_SkipsEnvironment()
    {
        var envData = new Dictionary<string, object?>
        {
            ["description"] = "no values key here",
        };

        var config = new Config.Config(
            Id: "id-1",
            Key: "my_key",
            Name: "My Config",
            Description: null,
            Parent: null,
            Values: new(),
            Environments: new()
            {
                ["staging"] = envData,
            },
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);

        Assert.DoesNotContain("staging", entry.EnvValues.Keys);
    }

    [Fact]
    public void ToChainEntry_EnvWithNonDictValues_SkipsEnvironment()
    {
        // "values" key exists but its value is not a dict (e.g., a string)
        var envData = new Dictionary<string, object?>
        {
            ["values"] = "not a dict",
        };

        var config = new Config.Config(
            Id: "id-1",
            Key: "my_key",
            Name: "My Config",
            Description: null,
            Parent: null,
            Values: new(),
            Environments: new()
            {
                ["staging"] = envData,
            },
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);

        Assert.DoesNotContain("staging", entry.EnvValues.Keys);
    }

    [Fact]
    public void ToChainEntry_WithJsonElementValues_Normalizes()
    {
        var je = JsonDocument.Parse("\"normalized\"").RootElement;
        var config = new Config.Config(
            Id: "id-1",
            Key: "my_key",
            Name: "My Config",
            Description: null,
            Parent: null,
            Values: new() { ["field"] = je },
            Environments: new(),
            CreatedAt: null,
            UpdatedAt: null);

        var entry = Resolver.ToChainEntry(config);
        Assert.Equal("normalized", entry.Values["field"]);
    }
}
