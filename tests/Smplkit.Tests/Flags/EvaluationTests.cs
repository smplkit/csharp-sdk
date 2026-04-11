using Smplkit.Flags;
using Xunit;

namespace Smplkit.Tests.Flags;

public class EvaluationTests
{
    private static Dictionary<string, object?> MakeFlagDef(
        object? defaultValue,
        Dictionary<string, object?>? environments = null)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = "test-flag",
            ["name"] = "Test Flag",
            ["type"] = "BOOLEAN",
            ["default"] = defaultValue,
            ["values"] = new List<Dictionary<string, object?>>(),
            ["description"] = null,
            ["environments"] = environments ?? new Dictionary<string, object?>(),
        };
    }

    private static Dictionary<string, object?> MakeEnvConfig(
        bool enabled,
        object? envDefault = null,
        List<object?>? rules = null)
    {
        var config = new Dictionary<string, object?>
        {
            ["enabled"] = enabled,
        };
        if (envDefault is not null)
            config["default"] = envDefault;
        if (rules is not null)
            config["rules"] = rules;
        return config;
    }

    private static Dictionary<string, object?> MakeRule(
        Dictionary<string, object?> logic,
        object? value)
    {
        return new Dictionary<string, object?>
        {
            ["description"] = "test rule",
            ["logic"] = logic,
            ["value"] = value,
        };
    }

    // ---------------------------------------------------------------
    // Flag default returns
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_ReturnsDefault_WhenEnvironmentNotFound()
    {
        var flagDef = MakeFlagDef(
            defaultValue: false,
            environments: new Dictionary<string, object?>
            {
                ["production"] = MakeEnvConfig(true),
            });

        var result = FlagsClient.EvaluateFlag(flagDef, "staging", new Dictionary<string, object?>());

        Assert.Equal(false, result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsDefault_WhenEnvironmentIsNull()
    {
        var flagDef = MakeFlagDef(defaultValue: "default-val");

        var result = FlagsClient.EvaluateFlag(flagDef, null, new Dictionary<string, object?>());

        Assert.Equal("default-val", result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsFallback_WhenEnvironmentDisabled()
    {
        var flagDef = MakeFlagDef(
            defaultValue: false,
            environments: new Dictionary<string, object?>
            {
                ["staging"] = MakeEnvConfig(enabled: false, envDefault: true),
            });

        var result = FlagsClient.EvaluateFlag(flagDef, "staging", new Dictionary<string, object?>());

        // envDefault (true) takes priority over flagDefault (false) for fallback
        Assert.Equal(true, result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsFlagDefault_WhenDisabledAndNoEnvDefault()
    {
        var flagDef = MakeFlagDef(
            defaultValue: "flag-default",
            environments: new Dictionary<string, object?>
            {
                ["staging"] = MakeEnvConfig(enabled: false),
            });

        var result = FlagsClient.EvaluateFlag(flagDef, "staging", new Dictionary<string, object?>());

        Assert.Equal("flag-default", result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsFallback_WhenNoRulesMatch()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "enterprise"
                    }
                },
                "enterprise-value"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, envDefault: "env-default", rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "free" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);

        Assert.Equal("env-default", result);
    }

    // ---------------------------------------------------------------
    // Rule matching
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_ReturnsRuleValue_WhenRuleMatches()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "enterprise"
                    }
                },
                true),
        };
        var flagDef = MakeFlagDef(
            defaultValue: false,
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "enterprise" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);

        Assert.Equal(true, result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsFirstMatchingRule()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "enterprise"
                    }
                },
                "enterprise-value"),
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "pro"
                    }
                },
                "pro-value"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "enterprise" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);

        Assert.Equal("enterprise-value", result);
    }

    [Fact]
    public void EvaluateFlag_FallsThroughToNextRule()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "enterprise"
                    }
                },
                "enterprise-value"),
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "pro"
                    }
                },
                "pro-value"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "pro" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);

        Assert.Equal("pro-value", result);
    }

    [Fact]
    public void EvaluateFlag_EmptyLogicDict_IsSkipped()
    {
        var rules = new List<object?>
        {
            MakeRule(new Dictionary<string, object?>(), "should-skip"),
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "pro"
                    }
                },
                "pro-value"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "pro" },
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", evalDict);

        Assert.Equal("pro-value", result);
    }

    [Fact]
    public void EvaluateFlag_MultipleConditions_AllMustMatch()
    {
        var logic = new Dictionary<string, object?>
        {
            ["and"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "enterprise"
                    }
                },
                new Dictionary<string, object?>
                {
                    ["=="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.region" },
                        "us"
                    }
                },
            }
        };
        var rules = new List<object?> { MakeRule(logic, "matched") };
        var flagDef = MakeFlagDef(
            defaultValue: "default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        // Both conditions match
        var evalDictMatch = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "enterprise", ["region"] = "us" },
        };
        Assert.Equal("matched", FlagsClient.EvaluateFlag(flagDef, "prod", evalDictMatch));

        // One condition fails
        var evalDictFail = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "enterprise", ["region"] = "eu" },
        };
        Assert.Equal("default", FlagsClient.EvaluateFlag(flagDef, "prod", evalDictFail));
    }

    // ---------------------------------------------------------------
    // Various operators
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_NotEqualOperator()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["!="] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.plan" },
                        "free"
                    }
                },
                "paid"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["plan"] = "enterprise" },
        };

        Assert.Equal("paid", FlagsClient.EvaluateFlag(flagDef, "prod", evalDict));
    }

    [Fact]
    public void EvaluateFlag_GreaterThanOperator()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    [">"] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.score" },
                        50
                    }
                },
                "high"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "low",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var highScore = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["score"] = 75 },
        };
        Assert.Equal("high", FlagsClient.EvaluateFlag(flagDef, "prod", highScore));

        var lowScore = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["score"] = 25 },
        };
        Assert.Equal("low", FlagsClient.EvaluateFlag(flagDef, "prod", lowScore));
    }

    [Fact]
    public void EvaluateFlag_LessThanOperator()
    {
        var rules = new List<object?>
        {
            MakeRule(
                new Dictionary<string, object?>
                {
                    ["<"] = new object?[]
                    {
                        new Dictionary<string, object?> { ["var"] = "user.score" },
                        10
                    }
                },
                "very-low"),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "normal",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = MakeEnvConfig(enabled: true, rules: rules),
            });

        var evalDict = new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["score"] = 5 },
        };
        Assert.Equal("very-low", FlagsClient.EvaluateFlag(flagDef, "prod", evalDict));
    }

    // ---------------------------------------------------------------
    // Edge cases
    // ---------------------------------------------------------------

    [Fact]
    public void EvaluateFlag_ReturnsDefault_WhenEnvironmentsDictMissing()
    {
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = "default-val",
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());

        Assert.Equal("default-val", result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsDefault_WhenEnvironmentsIsNotDict()
    {
        var flagDef = new Dictionary<string, object?>
        {
            ["id"] = "test",
            ["default"] = 42,
            ["environments"] = "not-a-dict",
        };

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());

        Assert.Equal(42, result);
    }

    [Fact]
    public void EvaluateFlag_ReturnsFallback_WhenEnabledKeyMissing()
    {
        // Environment config without "enabled" key should return fallback
        var envConfig = new Dictionary<string, object?>
        {
            ["default"] = "env-default-val",
            ["rules"] = new List<object?>(),
        };
        var flagDef = MakeFlagDef(
            defaultValue: "flag-default",
            environments: new Dictionary<string, object?>
            {
                ["prod"] = envConfig,
            });

        var result = FlagsClient.EvaluateFlag(flagDef, "prod", new Dictionary<string, object?>());

        Assert.Equal("env-default-val", result);
    }
}
