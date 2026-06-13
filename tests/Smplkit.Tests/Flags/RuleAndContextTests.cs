using Smplkit;
using Xunit;

namespace Smplkit.Tests.Flags;

public class RuleAndContextTests
{
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

    // ---------------------------------------------------------------
    // Rule builder — typed Op operator
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(Op.Eq, "==")]
    [InlineData(Op.Neq, "!=")]
    [InlineData(Op.Gt, ">")]
    [InlineData(Op.Gte, ">=")]
    [InlineData(Op.Lt, "<")]
    [InlineData(Op.Lte, "<=")]
    [InlineData(Op.In, "in")]
    public void Rule_TypedOp_MapsToWireOperator(Op op, string wire)
    {
        var rule = new Rule($"Test {op}")
            .When("user.score", op, 100)
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey(wire));

        var operands = (object?[])logic[wire]!;
        var varRef = (Dictionary<string, object?>)operands[0]!;
        Assert.Equal("user.score", varRef["var"]);
        Assert.Equal(100, operands[1]);
    }

    [Fact]
    public void Rule_TypedOp_Contains_ReversesOperands()
    {
        // Op.Contains routes through the raw-string overload's "contains" →
        // reversed-"in" rewrite, so the JSON Logic operator is "in".
        var rule = new Rule("Contains via Op")
            .When("user.tags", Op.Contains, "beta")
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey("in"));

        var operands = (object?[])logic["in"]!;
        Assert.Equal("beta", operands[0]);
        var varRef = (Dictionary<string, object?>)operands[1]!;
        Assert.Equal("user.tags", varRef["var"]);
    }

    [Fact]
    public void Rule_TypedOp_AndRawString_AreEquivalent()
    {
        var typed = new Rule("e").When("user.plan", Op.Eq, "enterprise").Serve(true).Build();
        var raw = new Rule("e").When("user.plan", "==", "enterprise").Serve(true).Build();

        var typedLogic = (Dictionary<string, object?>)typed["logic"]!;
        var rawLogic = (Dictionary<string, object?>)raw["logic"]!;
        Assert.Equal(rawLogic.Keys, typedLogic.Keys);
    }

    // ---------------------------------------------------------------
    // Rule builder — required-environment ctor
    // ---------------------------------------------------------------

    [Fact]
    public void Rule_EnvironmentCtor_SetsEnvironment()
    {
        var rule = new Rule("Enterprise rule", "production")
            .When("user.plan", Op.Eq, "enterprise")
            .Serve(true)
            .Build();

        Assert.Equal("Enterprise rule", rule["description"]);
        Assert.Equal("production", rule["environment"]);
    }

    [Fact]
    public void Rule_EnvironmentCtor_OverriddenByEnvironmentMethod()
    {
        var rule = new Rule("r", "production")
            .Environment("staging")
            .Serve(true)
            .Build();

        Assert.Equal("staging", rule["environment"]);
    }

    // ---------------------------------------------------------------
    // Rule builder — raw JSON Logic When overload
    // ---------------------------------------------------------------

    [Fact]
    public void Rule_WhenJsonLogic_SingleExpression_UsedAsLogic()
    {
        var orExpr = new Dictionary<string, object?>
        {
            ["or"] = new object?[]
            {
                new Dictionary<string, object?> { ["=="] = new object?[] { new Dictionary<string, object?> { ["var"] = "user.plan" }, "pro" } },
                new Dictionary<string, object?> { ["=="] = new object?[] { new Dictionary<string, object?> { ["var"] = "user.plan" }, "enterprise" } },
            },
        };

        var rule = new Rule("Pro or enterprise", "production")
            .When(orExpr)
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey("or"));
    }

    [Fact]
    public void Rule_WhenJsonLogic_CombinesWithTypedWhen_UnderAnd()
    {
        var rule = new Rule("Mixed")
            .When("user.plan", Op.Eq, "enterprise")
            .When(new Dictionary<string, object?>
            {
                ["!"] = new Dictionary<string, object?> { ["var"] = "user.banned" },
            })
            .Serve(true)
            .Build();

        var logic = (Dictionary<string, object?>)rule["logic"]!;
        Assert.True(logic.ContainsKey("and"));
        var conditions = (object?[])logic["and"]!;
        Assert.Equal(2, conditions.Length);
    }
}
