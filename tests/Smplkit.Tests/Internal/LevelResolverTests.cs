using Smplkit;
using Smplkit.Internal;
using Xunit;

namespace Smplkit.Tests.Internal;

/// <summary>
/// Mirrors <c>python-sdk/tests/unit/logging/test_resolution.py</c> — the
/// flagship reference implementation. Every step of the resolution chain
/// (env override → base level → group chain → dot-notation ancestry → INFO
/// fallback) is covered, plus cycle protection and the debug-only source
/// detector.
/// </summary>
public class LevelResolverTests
{
    private static Dictionary<string, LevelEntry> Loggers(params (string Id, LevelEntry Entry)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Entry);

    private static Dictionary<string, LevelEntry> Groups(params (string Id, LevelEntry Entry)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Entry);

    private static LevelEntry Entry(
        LogLevel? level = null,
        string? group = null,
        IDictionary<string, Dictionary<string, object?>>? environments = null)
        => new(level, group, environments);

    private static IDictionary<string, Dictionary<string, object?>> Envs(params (string Env, LogLevel? Level)[] pairs)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>();
        foreach (var (env, lvl) in pairs)
            result[env] = new Dictionary<string, object?> { ["level"] = lvl?.ToWireString() };
        return result;
    }

    // ------------------------------------------------------------------
    // Basic — logger's own env override / base level / fallback
    // ------------------------------------------------------------------

    [Fact]
    public void LoggerEnvLevel_Wins()
    {
        var loggers = Loggers(
            ("com.example.sql", Entry(LogLevel.Debug, environments: Envs(("production", LogLevel.Error)))));
        Assert.Equal(LogLevel.Error,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    [Fact]
    public void LoggerBaseLevel_WhenNoEnvOverride()
    {
        var loggers = Loggers(("com.example.sql", Entry(LogLevel.Debug)));
        Assert.Equal(LogLevel.Debug,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    [Fact]
    public void LoggerBaseLevel_WhenEnvOverrideTargetsDifferentEnv()
    {
        var loggers = Loggers(
            ("com.example.sql", Entry(LogLevel.Debug, environments: Envs(("staging", LogLevel.Trace)))));
        Assert.Equal(LogLevel.Debug,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    [Fact]
    public void Fallback_IsInfo()
    {
        Assert.Equal(LogLevel.Info,
            LevelResolver.Resolve("unknown.logger", "production", Loggers(), Groups()));
    }

    // ------------------------------------------------------------------
    // Group chain
    // ------------------------------------------------------------------

    [Fact]
    public void GroupEnvLevel_Wins()
    {
        var loggers = Loggers(("com.example.sql", Entry(group: "group-1")));
        var groups = Groups(
            ("group-1", Entry(LogLevel.Warn, environments: Envs(("production", LogLevel.Error)))));
        Assert.Equal(LogLevel.Error,
            LevelResolver.Resolve("com.example.sql", "production", loggers, groups));
    }

    [Fact]
    public void GroupBaseLevel_WhenNoEnvOverride()
    {
        var loggers = Loggers(("com.example.sql", Entry(group: "group-1")));
        var groups = Groups(("group-1", Entry(LogLevel.Warn)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("com.example.sql", "production", loggers, groups));
    }

    [Fact]
    public void NestedGroupChain_WalksToAncestor()
    {
        var loggers = Loggers(("com.example.sql", Entry(group: "group-child")));
        var groups = Groups(
            ("group-child", Entry(group: "group-parent")),
            ("group-parent", Entry(LogLevel.Fatal)));
        Assert.Equal(LogLevel.Fatal,
            LevelResolver.Resolve("com.example.sql", "production", loggers, groups));
    }

    [Fact]
    public void GroupCycle_DoesNotInfiniteLoop()
    {
        var loggers = Loggers(("com.example.sql", Entry(group: "group-a")));
        var groups = Groups(
            ("group-a", Entry(group: "group-b")),
            ("group-b", Entry(group: "group-a")));
        Assert.Equal(LogLevel.Info,
            LevelResolver.Resolve("com.example.sql", "production", loggers, groups));
    }

    [Fact]
    public void GroupIdNotInGroupsDict_FallsThrough()
    {
        var loggers = Loggers(("com.example", Entry(group: "missing-group-id")));
        Assert.Equal(LogLevel.Info,
            LevelResolver.Resolve("com.example", "production", loggers, Groups()));
    }

    // ------------------------------------------------------------------
    // Dot-notation ancestry
    // ------------------------------------------------------------------

    [Fact]
    public void Parent_LoggerLevel_AppliedToChild()
    {
        var loggers = Loggers(("com.example", Entry(LogLevel.Warn)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    [Fact]
    public void Grandparent_LoggerLevel_AppliedToGrandchild()
    {
        var loggers = Loggers(("com", Entry(LogLevel.Error)));
        Assert.Equal(LogLevel.Error,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    [Fact]
    public void ClosestAncestor_Wins()
    {
        var loggers = Loggers(
            ("com", Entry(LogLevel.Error)),
            ("com.example", Entry(LogLevel.Debug)));
        Assert.Equal(LogLevel.Debug,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    [Fact]
    public void GroupOnDirectLogger_TakesPrecedenceOverDotAncestor()
    {
        var loggers = Loggers(
            ("com.example.sql", Entry(group: "group-1")),
            ("com.example", Entry(LogLevel.Debug)));
        var groups = Groups(("group-1", Entry(LogLevel.Error)));
        Assert.Equal(LogLevel.Error,
            LevelResolver.Resolve("com.example.sql", "production", loggers, groups));
    }

    [Fact]
    public void AncestorEnvOverride_AppliesToDescendant()
    {
        var loggers = Loggers(
            ("com.example", Entry(LogLevel.Debug, environments: Envs(("production", LogLevel.Fatal)))));
        Assert.Equal(LogLevel.Fatal,
            LevelResolver.Resolve("com.example.sql", "production", loggers, Groups()));
    }

    // ------------------------------------------------------------------
    // Edge cases
    // ------------------------------------------------------------------

    [Fact]
    public void EmptyEnvironmentsObject_DoesNotThrow()
    {
        var loggers = Loggers(
            ("test", Entry(LogLevel.Warn, environments: new Dictionary<string, Dictionary<string, object?>>())));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "prod", loggers, Groups()));
    }

    [Fact]
    public void NullEnvironmentsObject_DoesNotThrow()
    {
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: null)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "prod", loggers, Groups()));
    }

    [Fact]
    public void EnvObjectWithoutLevelKey_IsIgnored()
    {
        var envs = new Dictionary<string, Dictionary<string, object?>>
        {
            ["production"] = new Dictionary<string, object?> { ["other"] = "ignored" },
        };
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: envs)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "production", loggers, Groups()));
    }

    [Fact]
    public void EnvObjectWithNullLevelValue_IsIgnored()
    {
        var envs = new Dictionary<string, Dictionary<string, object?>>
        {
            ["production"] = new Dictionary<string, object?> { ["level"] = null },
        };
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: envs)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "production", loggers, Groups()));
    }

    [Fact]
    public void EnvObjectWithBogusLevelString_IsIgnored()
    {
        var envs = new Dictionary<string, Dictionary<string, object?>>
        {
            ["production"] = new Dictionary<string, object?> { ["level"] = "NOT-A-LEVEL" },
        };
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: envs)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "production", loggers, Groups()));
    }

    [Fact]
    public void EnvLevel_AcceptsRawEnumValue()
    {
        var envs = new Dictionary<string, Dictionary<string, object?>>
        {
            ["production"] = new Dictionary<string, object?> { ["level"] = LogLevel.Error },
        };
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: envs)));
        Assert.Equal(LogLevel.Error,
            LevelResolver.Resolve("test", "production", loggers, Groups()));
    }

    [Fact]
    public void EnvLevel_OtherTypeValue_IsIgnored()
    {
        var envs = new Dictionary<string, Dictionary<string, object?>>
        {
            ["production"] = new Dictionary<string, object?> { ["level"] = 42 },
        };
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: envs)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "production", loggers, Groups()));
    }

    [Fact]
    public void EnvDataNull_IsIgnored()
    {
        var envs = new Dictionary<string, Dictionary<string, object?>> { ["production"] = null! };
        var loggers = Loggers(("test", Entry(LogLevel.Warn, environments: envs)));
        Assert.Equal(LogLevel.Warn,
            LevelResolver.Resolve("test", "production", loggers, Groups()));
    }

    // ------------------------------------------------------------------
    // FindResolutionSource — debug-only metadata helper
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, LevelEntry> _sourceLoggers = new()
    {
        ["with.env"] = Entry(LogLevel.Debug, environments: Envs(("production", LogLevel.Error))),
        ["with.base"] = Entry(LogLevel.Warn),
        ["with.group"] = Entry(group: "g1"),
        ["no.resolution"] = Entry(),
    };

    private static readonly Dictionary<string, LevelEntry> _sourceGroups = new()
    {
        ["g1"] = Entry(LogLevel.Debug),
    };

    [Fact]
    public void FindResolutionSource_EnvOverride()
    {
        Assert.Equal("env override \"production\"",
            LevelResolver.FindResolutionSource("with.env", "production", _sourceLoggers, _sourceGroups));
    }

    [Fact]
    public void FindResolutionSource_BaseLevel()
    {
        Assert.Equal("base level",
            LevelResolver.FindResolutionSource("with.base", "production", _sourceLoggers, _sourceGroups));
    }

    [Fact]
    public void FindResolutionSource_Group()
    {
        Assert.Equal("group \"g1\"",
            LevelResolver.FindResolutionSource("with.group", "production", _sourceLoggers, _sourceGroups));
    }

    [Fact]
    public void FindResolutionSource_Unknown_WhenLoggerHasNoSource()
    {
        Assert.Equal("unknown",
            LevelResolver.FindResolutionSource("no.resolution", "production", _sourceLoggers, _sourceGroups));
    }

    [Fact]
    public void FindResolutionSource_NotFound_WhenLoggerMissing()
    {
        Assert.Equal("not found",
            LevelResolver.FindResolutionSource("missing", "production", Loggers(), Groups()));
    }
}
