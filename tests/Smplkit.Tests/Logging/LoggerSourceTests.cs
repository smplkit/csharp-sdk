using System.Linq;
using Smplkit;
using Smplkit.Logging;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LoggerSourceTests
{
    [Fact]
    public void Constructor_NameAndResolvedLevel_DefaultsRest()
    {
        var src = new LoggerSource("sqlalchemy.engine", LogLevel.Warn);
        Assert.Equal("sqlalchemy.engine", src.Name);
        Assert.Equal(LogLevel.Warn, src.ResolvedLevel);
        Assert.Null(src.Service);
        Assert.Null(src.Environment);
        Assert.Null(src.Level);
    }

    [Fact]
    public void Constructor_AllArgs_StoresThem()
    {
        var src = new LoggerSource(
            name: "httpx",
            resolvedLevel: LogLevel.Warn,
            service: "billing",
            environment: "production",
            level: LogLevel.Info);

        Assert.Equal("httpx", src.Name);
        Assert.Equal(LogLevel.Warn, src.ResolvedLevel);
        Assert.Equal("billing", src.Service);
        Assert.Equal("production", src.Environment);
        Assert.Equal(LogLevel.Info, src.Level);
    }

    [Fact]
    public void Constructor_ResolvedLevel_IsRequired_SecondPositional_NoDefault()
    {
        // Guards the canonical parity contract: resolved_level is a required
        // field (no default), positioned immediately after name. If anyone
        // re-introduces a default this fails — matching python LoggerSource.
        var ctor = typeof(LoggerSource).GetConstructors().Single();
        var parameters = ctor.GetParameters();

        var resolved = parameters[1];
        Assert.Equal("resolvedLevel", resolved.Name);
        Assert.Equal(typeof(LogLevel), resolved.ParameterType);
        Assert.False(resolved.HasDefaultValue);
        Assert.False(resolved.IsOptional);

        // The remaining params are the optional ones, ordered after resolvedLevel.
        Assert.Equal(
            new[] { "name", "resolvedLevel", "service", "environment", "level" },
            parameters.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ResolvedLevel_Property_IsNonNullable()
    {
        // The CLR property type is the non-nullable enum, not Nullable<LogLevel>.
        var prop = typeof(LoggerSource).GetProperty(nameof(LoggerSource.ResolvedLevel))!;
        Assert.Equal(typeof(LogLevel), prop.PropertyType);
    }

    [Fact]
    public void ToString_IncludesNameServiceEnvironment()
    {
        var src = new LoggerSource("a", LogLevel.Info, "svc", "env");
        var str = src.ToString();
        Assert.Contains("LoggerSource", str);
        Assert.Contains("a", str);
        Assert.Contains("svc", str);
        Assert.Contains("env", str);
    }
}
