using System.Net;
using System.Text;
using System.Text.Json;
using Smplkit.Config;
using Smplkit.Tests.Helpers;
using Xunit;

namespace Smplkit.Tests.Config;

public class ResolverTests
{
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/vnd.api+json"),
        };

    /// <summary>
    /// Create a Config object for testing via ConfigClient.New, then set properties.
    /// </summary>
    private static Smplkit.Config.Config MakeConfig(
        string? id,
        string key,
        string name,
        string? description,
        string? parent,
        Dictionary<string, object?> items,
        Dictionary<string, Dictionary<string, object?>> environments)
    {
        var handler = new MockHttpMessageHandler(_ => Task.FromResult(JsonResponse("{}")));
        var httpClient = new HttpClient(handler);
        var smplClient = new SmplClient(TestData.DefaultOptions(), httpClient);

        var config = smplClient.Config.New(key, name, description, parent);
        config.Items = items;
        config.Environments = environments;
        config.Id = id;
        return config;
    }

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
        Assert.Equal(60, result["timeout"]);
        Assert.Equal("yes", result["child_only"]);
        Assert.Equal("yes", result["parent_only"]);
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
        Assert.Equal("gc_val", result["gc"]);
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
        var config = MakeConfig(
            id: "id-1", key: "my_key", name: "My Config",
            description: null, parent: null,
            items: new() { ["timeout"] = 30 },
            environments: new());

        var entry = Resolver.ToChainEntry(config);

        Assert.Equal("id-1", entry.Id);
        Assert.Equal(30, entry.Values["timeout"]);
        Assert.Empty(entry.EnvValues);
    }

    [Fact]
    public void ToChainEntry_WithEnvironments_ExtractsEnvValues()
    {
        var config = MakeConfig(
            id: "id-1", key: "my_key", name: "My Config",
            description: null, parent: null,
            items: new() { ["timeout"] = 30 },
            environments: new()
            {
                ["production"] = new() { ["retries"] = 5 },
            });

        var entry = Resolver.ToChainEntry(config);

        Assert.True(entry.EnvValues.ContainsKey("production"));
        Assert.Equal(5, entry.EnvValues["production"]["retries"]);
    }

    [Fact]
    public void ToChainEntry_EmptyEnvironment_IncludedInEntry()
    {
        var config = MakeConfig(
            id: "id-1", key: "my_key", name: "My Config",
            description: null, parent: null,
            items: new(),
            environments: new() { ["staging"] = new() });

        var entry = Resolver.ToChainEntry(config);

        Assert.Contains("staging", entry.EnvValues.Keys);
        Assert.Empty(entry.EnvValues["staging"]);
    }

    [Fact]
    public void ToChainEntry_EnvWithJsonElementValues_Normalizes()
    {
        var je = JsonDocument.Parse("42").RootElement;
        var config = MakeConfig(
            id: "id-1", key: "my_key", name: "My Config",
            description: null, parent: null,
            items: new(),
            environments: new() { ["staging"] = new() { ["count"] = je } });

        var entry = Resolver.ToChainEntry(config);

        Assert.Contains("staging", entry.EnvValues.Keys);
        Assert.Equal(42L, entry.EnvValues["staging"]["count"]);
    }

    [Fact]
    public void ToChainEntry_WithJsonElementValues_Normalizes()
    {
        var je = JsonDocument.Parse("\"normalized\"").RootElement;
        var config = MakeConfig(
            id: "id-1", key: "my_key", name: "My Config",
            description: null, parent: null,
            items: new() { ["field"] = je },
            environments: new());

        var entry = Resolver.ToChainEntry(config);
        Assert.Equal("normalized", entry.Values["field"]);
    }

    [Fact]
    public void Normalize_JsonElement_UndefinedKind_ReturnsNull()
    {
        var je = default(JsonElement);
        var result = Resolver.Normalize(je);
        Assert.Null(result);
    }

    [Fact]
    public void Normalize_JsonElement_MixedArray_NormalizesAll()
    {
        var je = JsonDocument.Parse("""[1, "two", true, null, 3.14, {"a": 1}]""").RootElement;
        var result = Resolver.Normalize(je);
        var arr = Assert.IsType<object?[]>(result);
        Assert.Equal(6, arr.Length);
        Assert.Equal(1L, arr[0]);
        Assert.Equal("two", arr[1]);
        Assert.Equal(true, arr[2]);
        Assert.Null(arr[3]);
        Assert.Equal(3.14, arr[4]);
        var nested = Assert.IsType<Dictionary<string, object?>>(arr[5]);
        Assert.Equal(1L, nested["a"]);
    }

    [Fact]
    public void NormalizeDict_WithNestedJsonElements_NormalizesRecursively()
    {
        var je = JsonDocument.Parse("""{"inner": [1, 2]}""").RootElement;
        var dict = new Dictionary<string, object?> { ["nested"] = je };
        var result = Resolver.NormalizeDict(dict);
        var nested = Assert.IsType<Dictionary<string, object?>>(result["nested"]);
        var arr = Assert.IsType<object?[]>(nested["inner"]);
        Assert.Equal(1L, arr[0]);
        Assert.Equal(2L, arr[1]);
    }

    [Fact]
    public void Normalize_BoolValue_PassesThrough()
    {
        Assert.Equal(true, Resolver.Normalize(true));
        Assert.Equal(false, Resolver.Normalize(false));
    }

    [Fact]
    public void Normalize_LongValue_PassesThrough()
    {
        Assert.Equal(42L, Resolver.Normalize(42L));
    }

    [Fact]
    public void Normalize_DoubleValue_PassesThrough()
    {
        Assert.Equal(3.14, Resolver.Normalize(3.14));
    }

    [Fact]
    public void DeepMerge_ThreeLevelNestedDicts_MergesCorrectly()
    {
        var @base = new Dictionary<string, object?>
        {
            ["l1"] = new Dictionary<string, object?>
            {
                ["l2"] = new Dictionary<string, object?>
                {
                    ["a"] = 1,
                    ["b"] = 2,
                }
            }
        };
        var @override = new Dictionary<string, object?>
        {
            ["l1"] = new Dictionary<string, object?>
            {
                ["l2"] = new Dictionary<string, object?>
                {
                    ["b"] = 99,
                    ["c"] = 3,
                }
            }
        };

        var result = Resolver.DeepMerge(@base, @override);
        var l1 = Assert.IsType<Dictionary<string, object?>>(result["l1"]);
        var l2 = Assert.IsType<Dictionary<string, object?>>(l1["l2"]);
        Assert.Equal(1, l2["a"]);
        Assert.Equal(99, l2["b"]);
        Assert.Equal(3, l2["c"]);
    }

    [Fact]
    public void Resolve_EnvOverridesAtMultipleLevels_ChildEnvWins()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "child",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new() { ["prod"] = new() { ["timeout"] = 100 } },
            },
            new()
            {
                Id = "parent",
                Values = new() { ["timeout"] = 10 },
                EnvValues = new() { ["prod"] = new() { ["timeout"] = 50 } },
            },
        };

        var result = Resolver.Resolve(chain, "prod");
        Assert.Equal(100, result["timeout"]);
    }

    [Fact]
    public void ToChainEntry_MultipleEnvironments_ExtractsAll()
    {
        var config = MakeConfig(
            id: "id-1", key: "key", name: "Name",
            description: null, parent: null,
            items: new(),
            environments: new()
            {
                ["production"] = new() { ["a"] = 1 },
                ["staging"] = new() { ["b"] = 2 },
            });

        var entry = Resolver.ToChainEntry(config);
        Assert.Equal(2, entry.EnvValues.Count);
        Assert.Equal(1, entry.EnvValues["production"]["a"]);
        Assert.Equal(2, entry.EnvValues["staging"]["b"]);
    }

    [Fact]
    public void ToChainEntry_EnvWithNullValue_IncludesEnvironment()
    {
        var config = MakeConfig(
            id: "id-1", key: "key", name: "Name",
            description: null, parent: null,
            items: new(),
            environments: new()
            {
                ["production"] = new() { ["setting"] = null },
            });

        var entry = Resolver.ToChainEntry(config);
        Assert.Contains("production", entry.EnvValues.Keys);
        Assert.Null(entry.EnvValues["production"]["setting"]);
    }

    [Fact]
    public void Normalize_JsonElement_LargeInt64_ReturnsLong()
    {
        var je = JsonDocument.Parse("9999999999").RootElement;
        var result = Resolver.Normalize(je);
        Assert.Equal(9999999999L, result);
    }

    [Fact]
    public void Normalize_JsonElement_EmptyObject_ReturnsEmptyDict()
    {
        var je = JsonDocument.Parse("{}").RootElement;
        var result = Resolver.Normalize(je);
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        Assert.Empty(dict);
    }

    [Fact]
    public void Normalize_JsonElement_EmptyArray_ReturnsEmptyArray()
    {
        var je = JsonDocument.Parse("[]").RootElement;
        var result = Resolver.Normalize(je);
        var arr = Assert.IsType<object?[]>(result);
        Assert.Empty(arr);
    }

    [Fact]
    public void Resolve_NoMatchingEnv_ReturnsBaseValuesOnly()
    {
        var chain = new List<ConfigChainEntry>
        {
            new()
            {
                Id = "c1",
                Values = new() { ["timeout"] = 30 },
                EnvValues = new() { ["staging"] = new() { ["timeout"] = 45 } },
            },
        };

        var result = Resolver.Resolve(chain, "production");
        Assert.Equal(30, result["timeout"]);
    }
}
