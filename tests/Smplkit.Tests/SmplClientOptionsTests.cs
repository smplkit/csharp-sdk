using Xunit;

namespace Smplkit.Tests;

public class SmplClientOptionsTests
{
    [Fact]
    public void Timeout_Default_Is30Seconds()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Equal(TimeSpan.FromSeconds(30), options.Timeout);
    }

    [Fact]
    public void Timeout_CanBeOverridden()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Timeout = TimeSpan.FromMinutes(2),
        };

        Assert.Equal(TimeSpan.FromMinutes(2), options.Timeout);
    }

    [Fact]
    public void ApiKey_IsSet()
    {
        var options = new SmplClientOptions { ApiKey = "sk_my_key", Environment = "test" };

        Assert.Equal("sk_my_key", options.ApiKey);
    }

    [Fact]
    public void Environment_IsSet()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "production" };

        Assert.Equal("production", options.Environment);
    }

    [Fact]
    public void Service_IsSet()
    {
        var options = new SmplClientOptions
        {
            ApiKey = "sk_test",
            Environment = "test",
            Service = "my-service",
        };

        Assert.Equal("my-service", options.Service);
    }

    [Fact]
    public void Service_DefaultIsNull()
    {
        var options = new SmplClientOptions { ApiKey = "sk_test", Environment = "test" };

        Assert.Null(options.Service);
    }
}
