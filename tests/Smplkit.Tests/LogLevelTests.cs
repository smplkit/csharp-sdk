using Xunit;

namespace Smplkit.Tests;

public class LogLevelTests
{
    // ------------------------------------------------------------------
    // ToWireString for each level
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Info, "INFO")]
    [InlineData(LogLevel.Warn, "WARN")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Fatal, "FATAL")]
    [InlineData(LogLevel.Silent, "SILENT")]
    public void ToWireString_ReturnsExpectedValue(LogLevel level, string expected)
    {
        Assert.Equal(expected, level.ToWireString());
    }

    // ------------------------------------------------------------------
    // ParseLogLevel for each wire string
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("TRACE", LogLevel.Trace)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("INFO", LogLevel.Info)]
    [InlineData("WARN", LogLevel.Warn)]
    [InlineData("ERROR", LogLevel.Error)]
    [InlineData("FATAL", LogLevel.Fatal)]
    [InlineData("SILENT", LogLevel.Silent)]
    public void ParseLogLevel_ReturnsExpectedLevel(string wire, LogLevel expected)
    {
        Assert.Equal(expected, LogLevelExtensions.ParseLogLevel(wire));
    }

    // ------------------------------------------------------------------
    // ArgumentOutOfRangeException for invalid enum value
    // ------------------------------------------------------------------

    [Fact]
    public void ToWireString_InvalidEnumValue_ThrowsArgumentOutOfRangeException()
    {
        var invalidLevel = (LogLevel)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => invalidLevel.ToWireString());
    }

    // ------------------------------------------------------------------
    // ArgumentException for unknown wire string
    // ------------------------------------------------------------------

    [Fact]
    public void ParseLogLevel_UnknownWireString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => LogLevelExtensions.ParseLogLevel("UNKNOWN"));
    }

    [Fact]
    public void ParseLogLevel_EmptyString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => LogLevelExtensions.ParseLogLevel(""));
    }

    [Fact]
    public void TryParseLogLevel_ReturnsLevelForKnownWireString()
    {
        Assert.Equal(LogLevel.Info, LogLevelExtensions.TryParseLogLevel("INFO"));
        Assert.Equal(LogLevel.Silent, LogLevelExtensions.TryParseLogLevel("SILENT"));
    }

    [Fact]
    public void TryParseLogLevel_ReturnsNullForUnknownWireString()
    {
        Assert.Null(LogLevelExtensions.TryParseLogLevel("UNKNOWN"));
        Assert.Null(LogLevelExtensions.TryParseLogLevel(""));
    }

    [Fact]
    public void TryParseLogLevel_ReturnsNullForNullInput()
    {
        Assert.Null(LogLevelExtensions.TryParseLogLevel(null));
    }

    [Fact]
    public void ParseLogLevel_LowercaseString_ThrowsArgumentException()
    {
        // Wire strings are uppercase only
        Assert.Throws<ArgumentException>(() => LogLevelExtensions.ParseLogLevel("info"));
    }
}
