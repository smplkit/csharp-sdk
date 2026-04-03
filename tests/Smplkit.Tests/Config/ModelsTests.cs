using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

public class ModelsTests
{
    // ------------------------------------------------------------------
    // Config record
    // ------------------------------------------------------------------

    [Fact]
    public void Config_Record_HasCorrectProperties()
    {
        var config = new Smplkit.Config.Config(
            Id: "id-1",
            Key: "my_key",
            Name: "My Config",
            Description: "A description",
            Parent: "parent-id",
            Items: new() { ["timeout"] = 30 },
            Environments: new()
            {
                ["production"] = new() { ["timeout"] = 60 },
            },
            CreatedAt: new DateTime(2024, 1, 15),
            UpdatedAt: new DateTime(2024, 1, 16));

        Assert.Equal("id-1", config.Id);
        Assert.Equal("my_key", config.Key);
        Assert.Equal("My Config", config.Name);
        Assert.Equal("A description", config.Description);
        Assert.Equal("parent-id", config.Parent);
        Assert.Equal(30, config.Items["timeout"]);
        Assert.True(config.Environments.ContainsKey("production"));
        Assert.Equal(new DateTime(2024, 1, 15), config.CreatedAt);
        Assert.Equal(new DateTime(2024, 1, 16), config.UpdatedAt);
    }

    [Fact]
    public void Config_Record_NullableFieldsCanBeNull()
    {
        var config = new Smplkit.Config.Config(
            Id: "id-1",
            Key: "key",
            Name: "name",
            Description: null,
            Parent: null,
            Items: new(),
            Environments: new(),
            CreatedAt: null,
            UpdatedAt: null);

        Assert.Null(config.Description);
        Assert.Null(config.Parent);
        Assert.Null(config.CreatedAt);
        Assert.Null(config.UpdatedAt);
    }

    // ------------------------------------------------------------------
    // CreateConfigOptions record
    // ------------------------------------------------------------------

    [Fact]
    public void CreateConfigOptions_RequiredAndOptionalFields()
    {
        var opts = new CreateConfigOptions
        {
            Name = "Test Config",
        };

        Assert.Equal("Test Config", opts.Name);
        Assert.Null(opts.Key);
        Assert.Null(opts.Description);
        Assert.Null(opts.Parent);
        Assert.Null(opts.Items);
        Assert.Null(opts.Environments);
    }

    [Fact]
    public void CreateConfigOptions_AllFields()
    {
        var vals = new Dictionary<string, object?> { ["a"] = 1 };
        var envs = new Dictionary<string, object?> { ["prod"] = new Dictionary<string, object?> { ["b"] = 2 } };

        var opts = new CreateConfigOptions
        {
            Name = "Test",
            Key = "test_key",
            Description = "desc",
            Parent = "parent-id",
            Items = vals,
            Environments = envs,
        };

        Assert.Equal("Test", opts.Name);
        Assert.Equal("test_key", opts.Key);
        Assert.Equal("desc", opts.Description);
        Assert.Equal("parent-id", opts.Parent);
        Assert.Same(vals, opts.Items);
        Assert.Same(envs, opts.Environments);
    }

    // ------------------------------------------------------------------
    // ConfigChangeEvent record
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigChangeEvent_HasCorrectProperties()
    {
        var evt = new ConfigChangeEvent("my_config", "timeout", 30, 60, "websocket");

        Assert.Equal("my_config", evt.ConfigKey);
        Assert.Equal("timeout", evt.ItemKey);
        Assert.Equal(30, evt.OldValue);
        Assert.Equal(60, evt.NewValue);
        Assert.Equal("websocket", evt.Source);
    }

    [Fact]
    public void ConfigChangeEvent_WithNullValues()
    {
        var evt = new ConfigChangeEvent("cfg", "key", null, null, "manual");

        Assert.Equal("cfg", evt.ConfigKey);
        Assert.Equal("key", evt.ItemKey);
        Assert.Null(evt.OldValue);
        Assert.Null(evt.NewValue);
        Assert.Equal("manual", evt.Source);
    }

    // ------------------------------------------------------------------
    // ConfigChainEntry
    // ------------------------------------------------------------------

    [Fact]
    public void ConfigChainEntry_DefaultValues()
    {
        var entry = new ConfigChainEntry { Id = "test-id" };

        Assert.Equal("test-id", entry.Id);
        Assert.NotNull(entry.Values);
        Assert.Empty(entry.Values);
        Assert.NotNull(entry.EnvValues);
        Assert.Empty(entry.EnvValues);
    }

    [Fact]
    public void ConfigChainEntry_CanSetValues()
    {
        var entry = new ConfigChainEntry
        {
            Id = "test-id",
            Values = new() { ["key"] = "value" },
            EnvValues = new()
            {
                ["prod"] = new() { ["key"] = "prod-value" },
            },
        };

        Assert.Equal("value", entry.Values["key"]);
        Assert.Equal("prod-value", entry.EnvValues["prod"]["key"]);
    }

    [Fact]
    public void ConfigChainEntry_ValuesAreMutable()
    {
        var entry = new ConfigChainEntry { Id = "id" };
        entry.Values = new() { ["a"] = 1 };
        entry.EnvValues = new() { ["env"] = new() { ["b"] = 2 } };

        Assert.Equal(1, entry.Values["a"]);
        Assert.Equal(2, entry.EnvValues["env"]["b"]);
    }
}
