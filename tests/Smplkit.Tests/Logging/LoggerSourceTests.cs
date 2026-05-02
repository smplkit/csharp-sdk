using Smplkit;
using Smplkit.Logging;
using Xunit;

namespace Smplkit.Tests.Logging;

public class LoggerSourceTests
{
    [Fact]
    public void Constructor_NameOnly_DefaultsRest()
    {
        var src = new LoggerSource("sqlalchemy.engine");
        Assert.Equal("sqlalchemy.engine", src.Name);
        Assert.Null(src.Service);
        Assert.Null(src.Environment);
        Assert.Null(src.ResolvedLevel);
        Assert.Null(src.Level);
    }

    [Fact]
    public void Constructor_AllArgs_StoresThem()
    {
        var src = new LoggerSource(
            name: "httpx",
            service: "billing",
            environment: "production",
            resolvedLevel: LogLevel.Warn,
            level: LogLevel.Info);

        Assert.Equal("httpx", src.Name);
        Assert.Equal("billing", src.Service);
        Assert.Equal("production", src.Environment);
        Assert.Equal(LogLevel.Warn, src.ResolvedLevel);
        Assert.Equal(LogLevel.Info, src.Level);
    }

    [Fact]
    public void ToString_IncludesNameServiceEnvironment()
    {
        var src = new LoggerSource("a", "svc", "env");
        var str = src.ToString();
        Assert.Contains("LoggerSource", str);
        Assert.Contains("a", str);
        Assert.Contains("svc", str);
        Assert.Contains("env", str);
    }
}
