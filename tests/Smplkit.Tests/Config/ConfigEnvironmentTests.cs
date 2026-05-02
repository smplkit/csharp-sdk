using Smplkit.Config;
using Xunit;

namespace Smplkit.Tests.Config;

/// <summary>
/// Tests for <see cref="ConfigEnvironment"/>, <see cref="ConfigItem"/>, and
/// <see cref="ItemType"/> per PR #127 rule 5 / 8.
/// </summary>
public class ConfigEnvironmentTests
{
    [Fact]
    public void ItemType_RoundTrip()
    {
        Assert.Equal("STRING", ItemType.String.ToWireString());
        Assert.Equal("NUMBER", ItemType.Number.ToWireString());
        Assert.Equal("BOOLEAN", ItemType.Boolean.ToWireString());
        Assert.Equal("JSON", ItemType.Json.ToWireString());

        Assert.Equal(ItemType.String, ItemTypeExtensions.Parse("STRING"));
        Assert.Equal(ItemType.Number, ItemTypeExtensions.Parse("NUMBER"));
        Assert.Equal(ItemType.Boolean, ItemTypeExtensions.Parse("BOOLEAN"));
        Assert.Equal(ItemType.Json, ItemTypeExtensions.Parse("JSON"));
    }

    [Fact]
    public void ConfigItem_HoldsAllFields()
    {
        var item = new ConfigItem("host", "localhost", ItemType.String, "DB host");
        Assert.Equal("host", item.Name);
        Assert.Equal("localhost", item.Value);
        Assert.Equal(ItemType.String, item.Type);
        Assert.Equal("DB host", item.Description);
    }
}
