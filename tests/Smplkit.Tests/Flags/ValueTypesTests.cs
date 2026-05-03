using Smplkit.Flags;
using Xunit;

namespace Smplkit.Tests.Flags;

/// <summary>
/// Tests for the read-only flag value types per PR #127 rule 5 / 8.
/// </summary>
public class FlagValueTypesTests
{
    [Fact]
    public void FlagValue_HasNameAndValue()
    {
        var value = new FlagValue("Red", "red");
        Assert.Equal("Red", value.Name);
        Assert.Equal("red", value.Value);
    }

    [Fact]
    public void FlagValue_RecordEquality_IsValueBased()
    {
        Assert.Equal(new FlagValue("A", 1), new FlagValue("A", 1));
        Assert.NotEqual(new FlagValue("A", 1), new FlagValue("A", 2));
    }

    [Fact]
    public void FlagRule_HasLogicValueDescription()
    {
        var logic = new Dictionary<string, object?> { ["=="] = new object?[] { "x", 1 } };
        var rule = new FlagRule(logic, Value: true, Description: "test");
        Assert.Equal(logic, rule.Logic);
        Assert.True((bool)rule.Value!);
        Assert.Equal("test", rule.Description);
    }

    [Fact]
    public void FlagEnvironment_DefaultsAreSensible()
    {
        var env = new FlagEnvironment();
        Assert.True(env.Enabled);
        Assert.Null(env.Default);
        Assert.NotNull(env.Rules);
        Assert.Empty(env.Rules);
    }

    [Fact]
    public void FlagEnvironment_RulesIsReadOnly()
    {
        var env = new FlagEnvironment(Rules: new[] { new FlagRule(new Dictionary<string, object?>(), 1) });
        // IReadOnlyList<T> does not expose Add — this is a compile-time enforcement.
        Assert.Single(env.Rules);
    }
}
