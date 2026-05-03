using Smplkit.Logging;
using Xunit;

namespace Smplkit.Tests.Logging;

/// <summary>
/// Tests for the read-only <see cref="LoggerEnvironment"/> per PR #127 rule 5 / 8.
/// </summary>
public class LoggerEnvironmentTests
{
    [Fact]
    public void DefaultsToNullLevel()
    {
        Assert.Null(new LoggerEnvironment().Level);
    }

    [Fact]
    public void StoresLevel()
    {
        Assert.Equal(LogLevel.Error, new LoggerEnvironment(LogLevel.Error).Level);
    }

    [Fact]
    public void RecordEqualityIsValueBased()
    {
        Assert.Equal(new LoggerEnvironment(LogLevel.Info), new LoggerEnvironment(LogLevel.Info));
        Assert.NotEqual(new LoggerEnvironment(LogLevel.Info), new LoggerEnvironment(LogLevel.Error));
    }
}
