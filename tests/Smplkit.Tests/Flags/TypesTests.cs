using Smplkit.Flags;
using Xunit;

namespace Smplkit.Tests.Flags;

public class TypesTests
{
    // ---------------------------------------------------------------
    // FlagType.ToWireString
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(FlagType.Boolean, "BOOLEAN")]
    [InlineData(FlagType.String, "STRING")]
    [InlineData(FlagType.Numeric, "NUMERIC")]
    [InlineData(FlagType.Json, "JSON")]
    public void ToWireString_ReturnsCorrectString(FlagType flagType, string expected)
    {
        Assert.Equal(expected, flagType.ToWireString());
    }

    [Fact]
    public void ToWireString_ThrowsForInvalidValue()
    {
        var invalid = (FlagType)999;
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.ToWireString());
    }

    // ---------------------------------------------------------------
    // FlagTypeExtensions.ParseFlagType
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("BOOLEAN", FlagType.Boolean)]
    [InlineData("STRING", FlagType.String)]
    [InlineData("NUMERIC", FlagType.Numeric)]
    [InlineData("JSON", FlagType.Json)]
    public void ParseFlagType_ReturnsCorrectEnum(string wireString, FlagType expected)
    {
        Assert.Equal(expected, FlagTypeExtensions.ParseFlagType(wireString));
    }

    [Fact]
    public void ParseFlagType_ThrowsForUnknownString()
    {
        Assert.Throws<ArgumentException>(() => FlagTypeExtensions.ParseFlagType("UNKNOWN"));
    }

    // ---------------------------------------------------------------
    // Context
    // ---------------------------------------------------------------

    [Fact]
    public void Context_Constructor_MinimalParams()
    {
        var ctx = new Context("user", "user-123");

        Assert.Equal("user", ctx.Type);
        Assert.Equal("user-123", ctx.Key);
        Assert.Null(ctx.Name);
        Assert.NotNull(ctx.Attributes);
        Assert.Empty(ctx.Attributes);
    }

    [Fact]
    public void Context_Constructor_WithAttributes()
    {
        var attrs = new Dictionary<string, object?> { ["plan"] = "enterprise", ["age"] = 30 };
        var ctx = new Context("user", "user-123", attrs);

        Assert.Equal("user", ctx.Type);
        Assert.Equal("user-123", ctx.Key);
        Assert.Equal("enterprise", ctx.Attributes["plan"]);
        Assert.Equal(30, ctx.Attributes["age"]);
        Assert.Null(ctx.Name);
    }

    [Fact]
    public void Context_Constructor_WithAllParams()
    {
        var attrs = new Dictionary<string, object?> { ["plan"] = "enterprise" };
        var ctx = new Context("user", "user-123", attrs, name: "Alice");

        Assert.Equal("user", ctx.Type);
        Assert.Equal("user-123", ctx.Key);
        Assert.Equal("Alice", ctx.Name);
        Assert.Equal("enterprise", ctx.Attributes["plan"]);
    }

    [Fact]
    public void Context_Constructor_NullAttributes_CreatesEmptyDict()
    {
        var ctx = new Context("device", "dev-1", null);

        Assert.NotNull(ctx.Attributes);
        Assert.Empty(ctx.Attributes);
    }

    [Fact]
    public void Context_ToString_IncludesAllFields()
    {
        var attrs = new Dictionary<string, object?> { ["plan"] = "pro", ["region"] = "us" };
        var ctx = new Context("user", "user-1", attrs, name: "Bob");

        var str = ctx.ToString();
        Assert.Contains("Type=user", str);
        Assert.Contains("Key=user-1", str);
        Assert.Contains("Name=Bob", str);
        Assert.Contains("plan", str);
        Assert.Contains("region", str);
    }

    [Fact]
    public void Context_ToString_NullName()
    {
        var ctx = new Context("user", "user-1");
        var str = ctx.ToString();
        Assert.Contains("Name=", str);
    }

    // ---------------------------------------------------------------
    // Rule builder
    // ---------------------------------------------------------------

    [Fact]
    public void Rule_SingleWhen_Build()
    {
        var rule = new Rule("Enable for enterprise")
            .When("user.plan", "==", "enterprise")
            .Serve(true)
            .Build();

        Assert.Equal("Enable for enterprise", rule["description"]);
        Assert.Equal(true, rule["value"]);
        Assert.NotNull(rule["logic"]);
        Assert.IsType<Dictionary<string, object?>>(rule["logic"]);

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey("=="));
    }

    [Fact]
    public void Rule_MultipleWhen_AndLogic()
    {
        var rule = new Rule("Enterprise in US")
            .When("user.plan", "==", "enterprise")
            .When("user.region", "==", "us")
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey("and"));
    }

    [Fact]
    public void Rule_WithEnvironment()
    {
        var rule = new Rule("Staging rule")
            .Environment("staging")
            .When("user.plan", "==", "pro")
            .Serve("enabled")
            .Build();

        Assert.Equal("staging", rule["environment"]);
    }

    [Fact]
    public void Rule_NoEnvironment_OmitsKey()
    {
        var rule = new Rule("No env rule")
            .When("user.plan", "==", "pro")
            .Serve(true)
            .Build();

        Assert.False(rule.ContainsKey("environment"));
    }

    [Fact]
    public void Rule_Serve_SetsValue()
    {
        var rule = new Rule("Serve string")
            .Serve("variant-a")
            .Build();

        Assert.Equal("variant-a", rule["value"]);
    }

    [Fact]
    public void Rule_Build_NoConditions_EmptyLogic()
    {
        var rule = new Rule("No conditions")
            .Serve(42)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.Empty(logic);
    }

    [Fact]
    public void Rule_ContainsOperator()
    {
        var rule = new Rule("Contains check")
            .When("user.tags", "contains", "beta")
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey("in"));

        var operands = (object?[])logic["in"]!;
        Assert.Equal("beta", operands[0]);
        // Second operand is the var reference
        var varRef = (Dictionary<string, object?>)operands[1]!;
        Assert.Equal("user.tags", varRef["var"]);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData(">")]
    [InlineData("<")]
    [InlineData(">=")]
    [InlineData("<=")]
    [InlineData("in")]
    public void Rule_VariousOperators_ProduceCorrectLogic(string op)
    {
        var rule = new Rule($"Test {op}")
            .When("user.score", op, 100)
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey(op));

        var operands = (object?[])logic[op]!;
        var varRef = (Dictionary<string, object?>)operands[0]!;
        Assert.Equal("user.score", varRef["var"]);
        Assert.Equal(100, operands[1]);
    }
}
