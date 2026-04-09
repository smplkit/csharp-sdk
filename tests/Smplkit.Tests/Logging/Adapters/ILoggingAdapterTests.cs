using Moq;
using Smplkit.Logging.Adapters;
using Xunit;

namespace Smplkit.Tests.Logging.Adapters;

public class ILoggingAdapterTests
{
    [Fact]
    public void MockAdapter_SatisfiesInterface()
    {
        var mock = new Mock<ILoggingAdapter>();
        mock.Setup(a => a.Name).Returns("test-adapter");
        mock.Setup(a => a.Discover()).Returns(new List<DiscoveredLogger>
        {
            new("test.logger", LogLevel.Info),
        });

        ILoggingAdapter adapter = mock.Object;

        Assert.Equal("test-adapter", adapter.Name);
        var discovered = adapter.Discover();
        Assert.Single(discovered);
        Assert.Equal("test.logger", discovered[0].Name);
        Assert.Equal(LogLevel.Info, discovered[0].Level);
    }

    [Fact]
    public void MockAdapter_ApplyLevel_CanBeCalled()
    {
        var mock = new Mock<ILoggingAdapter>();
        ILoggingAdapter adapter = mock.Object;

        adapter.ApplyLevel("test.logger", LogLevel.Error);

        mock.Verify(a => a.ApplyLevel("test.logger", LogLevel.Error), Times.Once);
    }

    [Fact]
    public void MockAdapter_InstallAndUninstallHook()
    {
        var mock = new Mock<ILoggingAdapter>();
        ILoggingAdapter adapter = mock.Object;

        Action<string, LogLevel> callback = (_, _) => { };
        adapter.InstallHook(callback);
        adapter.UninstallHook();

        mock.Verify(a => a.InstallHook(callback), Times.Once);
        mock.Verify(a => a.UninstallHook(), Times.Once);
    }
}
