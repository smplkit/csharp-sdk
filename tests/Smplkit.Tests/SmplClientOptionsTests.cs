using Xunit;

namespace Smplkit.Tests;

public class SmplClientOptionsTests
{
    [Fact]
    public void Timeout_Default_Is30Seconds()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test" };

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void Timeout_CanBeOverridden()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Timeout = TimeSpan.FromMinutes(2),
        };

        Assert.Equal(TimeSpan.FromMinutes(2), options.Timeout);
    }

    [Fact]
    public void ApiKey_IsSet()
    {
        var options = new SmplClientOptions { ApiKey = "sk_my_key" };

        Assert.Equal("sk_my_key", options.ApiKey);
    }
}
