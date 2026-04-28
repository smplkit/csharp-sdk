using System.Text.Json;
using Smplkit.Management;
using Xunit;

namespace Smplkit.Tests.Management;

// ---------------------------------------------------------------------------
// EnvironmentClassification
// ---------------------------------------------------------------------------

public class EnvironmentClassificationTests
{
    [Theory]
    [InlineData(EnvironmentClassification.Standard, "STANDARD")]
    [InlineData(EnvironmentClassification.AdHoc, "AD_HOC")]
    public void ToWireString_ReturnsExpected(EnvironmentClassification classification, string expected)
        => Assert.Equal(expected, classification.ToWireString());

    [Theory]
    [InlineData("STANDARD", EnvironmentClassification.Standard)]
    [InlineData("AD_HOC", EnvironmentClassification.AdHoc)]
    [InlineData("unknown", EnvironmentClassification.Standard)]
    [InlineData(null, EnvironmentClassification.Standard)]
    public void ParseClassification_ReturnsExpected(string? wire, EnvironmentClassification expected)
        => Assert.Equal(expected, EnvironmentClassificationExtensions.ParseClassification(wire));

    [Fact]
    public void ToWireString_InvalidValue_ThrowsArgumentOutOfRange()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ((EnvironmentClassification)99).ToWireString());
}

// ---------------------------------------------------------------------------
// ContextType model
// ---------------------------------------------------------------------------

public class ContextTypeModelTests
{
    private static ContextType MakeContextType(string id = "user", string name = "User")
    {
        // Use reflection to call internal constructor through the test helper
        return (ContextType)Activator.CreateInstance(
            typeof(ContextType),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { null, id, name, null, null, null },
            null)!;
    }

    [Fact]
    public void AddAttribute_AddsToDict()
    {
        var ct = MakeContextType();
        ct.AddAttribute("plan");
        Assert.True(ct.Attributes.ContainsKey("plan"));
        Assert.Empty(ct.Attributes["plan"]);
    }

    [Fact]
    public void AddAttribute_WithMetadata_StoresMetadata()
    {
        var ct = MakeContextType();
        ct.AddAttribute("region", new Dictionary<string, object?> { ["label"] = "Region" });
        Assert.Equal("Region", ct.Attributes["region"]["label"]);
    }

    [Fact]
    public void RemoveAttribute_RemovesFromDict()
    {
        var ct = MakeContextType();
        ct.AddAttribute("plan");
        ct.RemoveAttribute("plan");
        Assert.False(ct.Attributes.ContainsKey("plan"));
    }

    [Fact]
    public void RemoveAttribute_NonExistent_DoesNotThrow()
    {
        var ct = MakeContextType();
        ct.RemoveAttribute("nonexistent"); // Should not throw
    }

    [Fact]
    public void UpdateAttribute_ReplacesMetadata()
    {
        var ct = MakeContextType();
        ct.AddAttribute("plan", new Dictionary<string, object?> { ["label"] = "Old" });
        ct.UpdateAttribute("plan", new Dictionary<string, object?> { ["label"] = "New" });
        Assert.Equal("New", ct.Attributes["plan"]["label"]);
    }

    [Fact]
    public void UpdateAttribute_WithNullMetadata_SetsEmpty()
    {
        var ct = MakeContextType();
        ct.AddAttribute("plan");
        ct.UpdateAttribute("plan");
        Assert.Empty(ct.Attributes["plan"]);
    }

    [Fact]
    public void ToString_ContainsIdAndName()
    {
        var ct = MakeContextType("user", "User");
        Assert.Contains("user", ct.ToString());
        Assert.Contains("User", ct.ToString());
    }

    [Fact]
    public void SaveAsync_WithoutClient_ThrowsNullReferenceOrInvalidOperation()
    {
        var ct = MakeContextType();
        Assert.ThrowsAny<Exception>(() => ct.SaveAsync().GetAwaiter().GetResult());
    }
}

// ---------------------------------------------------------------------------
// ContextEntity model
// ---------------------------------------------------------------------------

public class ContextEntityModelTests
{
    private static ContextEntity Make(string type = "user", string key = "u1",
        string? name = null, Dictionary<string, object?>? attrs = null)
    {
        return (ContextEntity)Activator.CreateInstance(
            typeof(ContextEntity),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { type, key, name, attrs ?? new Dictionary<string, object?>(), null, null },
            null)!;
    }

    [Fact]
    public void Id_IsComposite()
    {
        var entity = Make("user", "u1");
        Assert.Equal("user:u1", entity.Id);
    }

    [Fact]
    public void Properties_Set()
    {
        var attrs = new Dictionary<string, object?> { ["plan"] = "pro" };
        var now = DateTime.UtcNow;
        var entity = (ContextEntity)Activator.CreateInstance(
            typeof(ContextEntity),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { "user", "u1", "Alice", attrs, now, now },
            null)!;

        Assert.Equal("user", entity.Type);
        Assert.Equal("u1", entity.Key);
        Assert.Equal("Alice", entity.Name);
        Assert.Equal("pro", entity.Attributes["plan"]);
        Assert.Equal(now, entity.CreatedAt);
        Assert.Equal(now, entity.UpdatedAt);
    }

    [Fact]
    public void ToString_ContainsTypeAndKey()
    {
        var entity = Make("account", "acct-1");
        Assert.Contains("account", entity.ToString());
        Assert.Contains("acct-1", entity.ToString());
    }
}

// ---------------------------------------------------------------------------
// AccountSettings model
// ---------------------------------------------------------------------------

public class AccountSettingsModelTests
{
    private static AccountSettings MakeSettings(Dictionary<string, object?>? data = null)
    {
        return (AccountSettings)Activator.CreateInstance(
            typeof(AccountSettings),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { null, data ?? new Dictionary<string, object?>() },
            null)!;
    }

    [Fact]
    public void EnvironmentOrder_Empty_WhenNotSet()
    {
        var settings = MakeSettings();
        Assert.Empty(settings.EnvironmentOrder);
    }

    [Fact]
    public void EnvironmentOrder_Get_FromListOfObject()
    {
        var settings = MakeSettings(new Dictionary<string, object?>
        {
            ["environment_order"] = new List<object?> { "production", "staging" },
        });
        var order = settings.EnvironmentOrder;
        Assert.Equal(new[] { "production", "staging" }, order);
    }

    [Fact]
    public void EnvironmentOrder_Get_FromListOfString()
    {
        var settings = MakeSettings(new Dictionary<string, object?>
        {
            ["environment_order"] = new List<string> { "production", "staging" },
        });
        Assert.Equal(new[] { "production", "staging" }, settings.EnvironmentOrder);
    }

    [Fact]
    public void EnvironmentOrder_Get_FromJsonElement()
    {
        using var doc = JsonDocument.Parse("""["production","staging"]""");
        var settings = MakeSettings(new Dictionary<string, object?>
        {
            ["environment_order"] = doc.RootElement.Clone(),
        });
        Assert.Equal(new[] { "production", "staging" }, settings.EnvironmentOrder);
    }

    [Fact]
    public void EnvironmentOrder_Set_UpdatesRaw()
    {
        var settings = MakeSettings();
        settings.EnvironmentOrder = new List<string> { "production", "staging", "development" };
        Assert.Equal(new[] { "production", "staging", "development" }, settings.EnvironmentOrder);
    }

    [Fact]
    public void Raw_ReturnsDataDict()
    {
        var data = new Dictionary<string, object?> { ["foo"] = "bar" };
        var settings = MakeSettings(data);
        Assert.Equal("bar", settings.Raw["foo"]);
    }

    [Fact]
    public void ApplyData_UpdatesInternally()
    {
        var settings = MakeSettings();
        settings.ApplyData(new Dictionary<string, object?> { ["environment_order"] = new List<object?> { "production" } });
        Assert.Equal(new[] { "production" }, settings.EnvironmentOrder);
    }

    [Fact]
    public void ToString_ContainsKeyCount()
    {
        var settings = MakeSettings(new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 });
        Assert.Contains("2", settings.ToString());
    }

    [Fact]
    public void EnvironmentOrder_Get_UnknownType_ReturnsEmpty()
    {
        // Tests the _ => new List<string>() fallback branch
        var settings = MakeSettings(new Dictionary<string, object?>
        {
            ["environment_order"] = 42, // integer — not any recognized list type
        });
        Assert.Empty(settings.EnvironmentOrder);
    }

    [Fact]
    public void SaveAsync_WithoutClient_ThrowsNullReferenceOrInvalidOperation()
    {
        var settings = MakeSettings();
        Assert.ThrowsAny<Exception>(() => settings.SaveAsync().GetAwaiter().GetResult());
    }
}

// ---------------------------------------------------------------------------
// SmplEnvironment model
// ---------------------------------------------------------------------------

public class SmplEnvironmentModelTests
{
    private static SmplEnvironment MakeEnv(string? id = "production", string name = "Production",
        EnvironmentClassification classification = EnvironmentClassification.Standard)
    {
        return (SmplEnvironment)Activator.CreateInstance(
            typeof(SmplEnvironment),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object?[] { null, id, name, null, classification, null, null },
            null)!;
    }

    [Fact]
    public void Properties_Set()
    {
        var env = MakeEnv("production", "Production", EnvironmentClassification.Standard);
        Assert.Equal("production", env.Id);
        Assert.Equal("Production", env.Name);
        Assert.Equal(EnvironmentClassification.Standard, env.Classification);
    }

    [Fact]
    public void ToString_ContainsIdAndName()
    {
        var env = MakeEnv("staging", "Staging");
        Assert.Contains("staging", env.ToString());
        Assert.Contains("Staging", env.ToString());
    }

    [Fact]
    public void SaveAsync_WithoutClient_ThrowsNullReferenceOrInvalidOperation()
    {
        var env = MakeEnv();
        Assert.ThrowsAny<Exception>(() => env.SaveAsync().GetAwaiter().GetResult());
    }

    [Fact]
    public void ColorAndCreatedAt_Setable()
    {
        var env = MakeEnv();
        env.Color = "#ff0000";
        env.Name = "Renamed";
        Assert.Equal("#ff0000", env.Color);
        Assert.Equal("Renamed", env.Name);
    }
}
